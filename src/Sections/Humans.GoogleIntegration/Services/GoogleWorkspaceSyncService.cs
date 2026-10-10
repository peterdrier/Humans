using Humans.Base.Attributes;
using Humans.GoogleIntegration.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Users.Contracts;
using Humans.Teams.Contracts;
using Humans.Base.Enums;
using Humans.Base.Helpers;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Services.Workspace;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// <see cref="IGoogleSyncService"/> impl: Workspace Drive reconciliation, Group provisioning, settings drift remediation.
/// Group membership reconciliation lives in <see cref="IGoogleGroupSync"/>.
/// </summary>
[CrossSectionWrite("Workspace sync writes Google email status back to the user.")]
internal sealed class GoogleWorkspaceSyncService(
    IGoogleGroupProvisioningClient groupProvisioning,
    IGoogleDrivePermissionsClient drivePermissions,
    IGoogleDirectoryClient directory,
    IGoogleResourceRepository resourceRepository,
    IGoogleSyncOutboxRepository googleSyncOutboxRepository,
    ITeamServiceRead teamService,
    IUserService userService,
    IUserEmailService userEmailService,
    IGoogleGroupSync googleGroupSync,
    IGoogleDriveSync googleDriveSync,
    IEnumerable<IGoogleDriveAccessSource> driveAccessSources,
    IAuditLogService auditLogService,
    IGoogleSyncLogService googleSyncLog,
    ISyncSettingsService syncSettingsService,
    IGoogleDriveAccessSyncScheduler driveAccessSyncScheduler,
    IOptions<GoogleWorkspaceOptions> options,
    IClock clock,
    ILogger<GoogleWorkspaceSyncService> logger) : IGoogleSyncService
{
    private readonly GoogleWorkspaceOptions _options = options.Value;


    /// <summary>GATEWAY: only path that adds a user to a Drive resource. Skips when GoogleDrive mode is None. <paramref name="permissionLevelOverride"/>: resolved max across teams sharing the resource; null = use resource's level.</summary>
    private async Task<GoogleResourceGrantOutcome> AddUserToDriveAsync(
        GoogleResource resource,
        string userEmail,
        Guid? userId,
        DrivePermissionLevel? permissionLevelOverride,
        GoogleSyncSource syncSource,
        CancellationToken cancellationToken)
    {
        var mode = await syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, cancellationToken);
        if (mode == SyncMode.None)
        {
            logger.LogDebug("Skipping AddUserToDrive — GoogleDrive sync mode is None");
            return GoogleResourceGrantOutcome.Deferred;
        }

        var effectiveLevel = permissionLevelOverride ?? resource.DrivePermissionLevel;
        var apiRole = effectiveLevel.ToApiRole();

        // Issue nobodies-collective/Humans#945 — Drive's permissions.create
        // rejects a plus-addressed local part (local+tag@domain) with an
        // opaque HTTP 400. Plus-addressing is only guaranteed equivalent on
        // Gmail (CanonicalizeGmail leaves non-Gmail domains untouched, since
        // granting the base address there could resolve to a different
        // Google identity than the one recorded on the user).
        var driveTargetEmail = EmailNormalization.CanonicalizeGmail(userEmail);

        var result = await drivePermissions.CreatePermissionAsync(resource.GoogleId, driveTargetEmail, apiRole, cancellationToken);

        switch (result.Outcome)
        {
            case DrivePermissionCreateOutcome.Created:
                await googleSyncLog.LogAsync(
                    GoogleSyncLogAction.AccessGranted, resource.Id,
                    $"Granted Drive access ({effectiveLevel}) to {userEmail} ({resource.Name})",
                    nameof(GoogleWorkspaceSyncService),
                    userEmail, apiRole, syncSource, success: true,
                    userId: userId, ct: cancellationToken);
                return GoogleResourceGrantOutcome.Accepted;

            case DrivePermissionCreateOutcome.AlreadyExists:
                logger.LogDebug("Permission already exists for {Email} on {GoogleId}", userEmail, resource.GoogleId);
                return GoogleResourceGrantOutcome.Accepted;

            case DrivePermissionCreateOutcome.Failed:
                logger.LogWarning(
                    "Google API error granting {Role} to {Email} on {GoogleId} — HTTP {Code}: {Message}",
                    apiRole, userEmail, resource.GoogleId,
                    result.Error?.StatusCode, result.Error?.RawMessage);
                // Issue nobodies-collective/Humans#1099 — record the failed
                // grant so it surfaces on the resource/human monitor pages
                // and in the GDPR export, matching GoogleGroupSyncService's
                // failure rows.
                await googleSyncLog.LogAsync(
                    GoogleSyncLogAction.AccessGranted, resource.Id,
                    $"Failed to grant Drive access ({effectiveLevel}) to {userEmail} ({resource.Name}): " +
                    $"HTTP {result.Error?.StatusCode} — {result.Error?.RawMessage}",
                    nameof(GoogleWorkspaceSyncService),
                    userEmail, apiRole, syncSource, success: false,
                    errorMessage: result.Error?.RawMessage,
                    userId: userId, ct: cancellationToken);
                await HandleDriveAddFailureAsync(resource, userEmail, result.Error, cancellationToken);
                return GoogleResourceGrantOutcome.Failed;
        }
        throw new InvalidOperationException($"Unknown Drive permission outcome {result.Outcome}");
    }

    /// <summary>
    /// Issue nobodies-collective/Humans#677 — when Drive's
    /// <c>permissions.create</c> returns a target-rejection (HTTP 400/403
    /// referencing "no Google account" / "SendNotificationEmail"), mark the
    /// owning user's <see cref="GoogleEmailStatus"/> as
    /// <see cref="GoogleEmailStatus.Rejected"/> so the orchestrator stops
    /// re-attempting until an admin clears the state. Mirrors the existing
    /// <c>GoogleGroupSyncService.HandleGroupAddFailureAsync</c> pattern.
    /// </summary>
    private async Task HandleDriveAddFailureAsync(
        GoogleResource resource,
        string userEmail,
        GoogleClientError? error,
        CancellationToken ct)
    {
        var statusCode = error?.StatusCode ?? 0;
        var rawMessage = error?.RawMessage ?? string.Empty;

        // Drive returns 400 when the recipient has no Google account on a
        // domain that supports notification-free sharing. 403 covers caller-
        // permission failures (not the target's problem) so we don't mark
        // those.
        if (statusCode != 400)
        {
            return;
        }

        // Drive-specific predicate only — generic phrases (sharing-policy) must not flip GoogleEmailStatus (#677).
        if (error?.IsDriveTargetRejection != true)
        {
            return;
        }

        var user = await userService.GetByEmailOrAlternateAsync(userEmail, ct);
        if (user is null || user.GoogleEmailStatus == GoogleEmailStatus.Rejected)
        {
            return;
        }

        await userService.TrySetGoogleEmailStatusFromSyncAsync(
            user.Id,
            GoogleEmailStatus.Rejected,
            ct);

        logger.LogWarning(
            "Google rejected target email {Email} while granting Drive permission on {GoogleId} - HTTP 400. " +
            "Google email status marked Rejected for the address. Google error: {ErrorMessage}",
            userEmail,
            resource.GoogleId,
            rawMessage);
    }

    /// <inheritdoc />
    public async Task<GoogleResourceGrantOutcome> AddUserToTeamResourcesAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default,
        GoogleSyncSource syncSource = GoogleSyncSource.ManualSync)
    {
        // Issue #635 (§15i): read UserEmails through the owning section
        // service (design-rules §2c) instead of traversing user.UserEmails
        // cross-domain.
        // Merge-fold redirect (issue peterdrier/Humans#646): an outbox event enqueued
        // before a merge is dequeued after it, so the id here may be an account that was
        // folded away — its team_members row and its UserEmails moved to the survivor.
        // Users resolves that forward (#1704), so provisioning reads the survivor's emails.
        var user = await userService.GetUserInfoAsync(userId, cancellationToken);

        var userEmails = user is null
            ? (IReadOnlyList<UserEmailRowSnapshot>)[]
            : await userEmailService.GetEntitiesByUserIdAsync(user.Id, cancellationToken);

        var googleEmail = userEmails
            .Where(e => e.IsVerified && e.IsGoogle)
            .Select(e => e.Email)
            .FirstOrDefault()
            ?? userEmails
                .Where(e => e.IsVerified && e.Provider != null)
                .OrderBy(e => e.Email, StringComparer.OrdinalIgnoreCase)
                .Select(e => e.Email)
                .FirstOrDefault();
        if (googleEmail is null)
        {
            if (user is null)
            {
                logger.LogWarning(
                    "Skipped Google provisioning for {UserId} on team {TeamId}: user no longer exists (likely deleted between outbox enqueue and dequeue)",
                    userId, teamId);
            }
            else
            {
                logger.LogWarning(
                    "Skipped Google provisioning for {UserId} on team {TeamId}: user exists but has no verified email",
                    userId, teamId);
            }
            return GoogleResourceGrantOutcome.Failed;
        }

        if (user!.GoogleEmailStatus == GoogleEmailStatus.Rejected)
        {
            logger.LogDebug("Skipping AddUserToTeamResources for user {UserId} — GoogleEmailStatus is Rejected", userId);
            return GoogleResourceGrantOutcome.Failed;
        }

        // From here on the id is the resolved human's, as the email above already is: the
        // team_members rows the permission level is read from moved to the survivor at
        // merge, and the grant is audited against the account that actually holds it.
        userId = user.Id;

        var team = await teamService.GetTeamAsync(teamId, cancellationToken);
        var resources = await resourceRepository.GetActiveByTeamIdAsync(teamId, cancellationToken);

        var outcomes = new List<GoogleResourceGrantOutcome>();
        foreach (var resource in resources)
        {
            if (resource.ResourceType == GoogleResourceType.Group)
            {
                // Group membership reconciliation owned by IGoogleGroupSync; this path handles Drive only.
                await RequestGoogleGroupSyncAsync(resource, team?.GoogleGroupEmail, cancellationToken);
                continue;
            }

            var level = await ResolvePermissionLevelForUserAsync(
                resource.GoogleId, userId, cancellationToken);
            if (level != DrivePermissionLevel.None)
                outcomes.Add(await AddUserToDriveAsync(resource, googleEmail, userId, level, syncSource, cancellationToken));
        }

        // Subteam member rollup: also add to parent department resources.
        if (team?.ParentTeamId is not null)
        {
            var parentTeam = await teamService.GetTeamAsync(team.ParentTeamId.Value, cancellationToken);
            var parentResources = await resourceRepository.GetActiveByTeamIdAsync(team.ParentTeamId.Value, cancellationToken);
            foreach (var resource in parentResources)
            {
                if (resource.ResourceType == GoogleResourceType.Group)
                {
                    await RequestGoogleGroupSyncAsync(resource, parentTeam?.GoogleGroupEmail, cancellationToken);
                    continue;
                }

                var level = await ResolvePermissionLevelForUserAsync(
                    resource.GoogleId, userId, cancellationToken);
                if (level != DrivePermissionLevel.None)
                    outcomes.Add(await AddUserToDriveAsync(resource, googleEmail, userId, level, syncSource, cancellationToken));
            }
        }
        return outcomes.Contains(GoogleResourceGrantOutcome.Failed)
            ? GoogleResourceGrantOutcome.Failed
            : outcomes.Contains(GoogleResourceGrantOutcome.Accepted)
                ? GoogleResourceGrantOutcome.Accepted
                : GoogleResourceGrantOutcome.Deferred;
    }

    private async Task RequestGoogleGroupSyncAsync(
        GoogleResource resource,
        string? teamGoogleGroupEmail,
        CancellationToken cancellationToken)
    {
        var groupKey = GoogleGroupKeyHelper.TryGetGroupKey(resource, teamGoogleGroupEmail, _options.Domain);
        if (groupKey is null)
        {
            logger.LogWarning(
                "Cannot request Google Group membership sync for resource {ResourceId}: group email could not be derived",
                resource.Id);
            return;
        }

        await googleGroupSync.RequestSyncAsync(groupKey, cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveUserFromTeamResourcesAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Individual user removal is a no-op. Drive removals are handled by
        // reconciliation, and Google Group membership is handled by
        // IGoogleGroupSync.
        logger.LogDebug(
            "Per-user removal deferred to reconciliation for user {UserId} team {TeamId}",
            userId, teamId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SyncPreviewResult> SyncResourcesByTypeAsync(
        GoogleResourceType resourceType,
        SyncAction action,
        CancellationToken cancellationToken = default,
        GoogleSyncSource syncSource = GoogleSyncSource.ManualSync)
    {
        if (resourceType == GoogleResourceType.Group)
            throw new InvalidOperationException("Google Group membership sync is handled by IGoogleGroupSync.");
        return googleDriveSync.ReconcileAllAsync(action, cancellationToken, resourceType, syncSource);
    }

    /// <inheritdoc />
    public async Task<ResourceSyncDiff> SyncSingleResourceAsync(
        Guid resourceId,
        SyncAction action,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("SyncSingleResource: resourceId={ResourceId}, action={Action}", resourceId, action);

        var resource = await resourceRepository.GetByIdAsync(resourceId, cancellationToken);
        if (resource is null)
        {
            return new ResourceSyncDiff
            {
                ResourceId = resourceId,
                ErrorMessage = "Resource not found"
            };
        }

        if (resource.ResourceType == GoogleResourceType.Group)
        {
            var team = await teamService.GetTeamAsync(resource.TeamId, cancellationToken);
            return await ReconcileGroupResourceAsync(resource, team?.GoogleGroupEmail, action, cancellationToken);
        }
        return await googleDriveSync.ReconcileOneAsync(
            resource.GoogleId, action, cancellationToken, GoogleSyncSource.ManualSync);
    }

    private async Task<ResourceSyncDiff> ReconcileGroupResourceAsync(
        GoogleResource resource,
        string? teamGoogleGroupEmail,
        SyncAction action,
        CancellationToken cancellationToken)
    {
        var groupKey = GoogleGroupKeyHelper.TryGetGroupKey(
            resource,
            teamGoogleGroupEmail,
            _options.Domain);

        return groupKey is null
            ? new ResourceSyncDiff
            {
                ResourceId = resource.Id,
                ResourceName = resource.Name,
                ResourceType = resource.ResourceType.ToString(),
                GoogleId = resource.GoogleId,
                Url = resource.Url,
                ErrorMessage = "Cannot determine Google group email for this resource"
            }
            : await googleGroupSync.ReconcileOneAsync(groupKey, action, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string> CreateSubfolderAsync(
        string parentFolderId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var mode = await syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, cancellationToken);
        if (mode == SyncMode.None)
        {
            throw new InvalidOperationException(
                $"Cannot create Drive subfolder '{name}' — Google Drive sync mode is set to None.");
        }

        var result = await drivePermissions.CreateFolderAsync(parentFolderId, name, cancellationToken);
        if (result.FolderId is null)
        {
            var message =
                $"Failed to create Drive subfolder '{name}' under {parentFolderId} " +
                $"(HTTP {result.Error?.StatusCode}): {result.Error?.RawMessage}";
            logger.LogWarning("{Message}", message);
            throw new InvalidOperationException(message);
        }

        logger.LogInformation(
            "Created Drive subfolder '{Name}' ({FolderId}) under {ParentFolderId}",
            name, result.FolderId, parentFolderId);
        return result.FolderId;
    }

    /// <inheritdoc />
    public Task RequestSyncAsync(string folderId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderId))
        {
            throw new ArgumentException("Folder id is required.", nameof(folderId));
        }

        driveAccessSyncScheduler.Enqueue(folderId.Trim());
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<GroupLinkResult> EnsureTeamGroupAsync(
        Guid teamId,
        bool confirmReactivation = false,
        CancellationToken cancellationToken = default)
    {
        var team = await teamService.GetTeamAsync(teamId, cancellationToken);
        if (team is null)
        {
            logger.LogWarning("Team {TeamId} not found for EnsureTeamGroupAsync", teamId);
            return GroupLinkResult.Ok();
        }

        var activeResources = await resourceRepository.GetActiveByTeamIdAsync(teamId, cancellationToken);
        var existingGroup = activeResources
            .Where(r => r.ResourceType == GoogleResourceType.Group)
            .OrderBy(r => r.ProvisionedAt)
            .FirstOrDefault();

        if (team.GoogleGroupPrefix is null)
        {
            if (existingGroup is not null)
            {
                await resourceRepository.DeactivateAsync(existingGroup.Id, cancellationToken);
                logger.LogInformation("Deactivated Group resource {ResourceId} for team {TeamId} (prefix cleared)",
                    existingGroup.Id, teamId);

                await auditLogService.LogAsync(
                    AuditAction.GoogleResourceDeactivated, nameof(GoogleResource), existingGroup.Id,
                    "Deactivated Google Group resource (prefix cleared)",
                    nameof(GoogleWorkspaceSyncService),
                    relatedEntityId: teamId, relatedEntityType: "Team");
            }
            else
            {
                logger.LogDebug("Team {TeamId} has no GoogleGroupPrefix and no active group, nothing to do", teamId);
            }
            return GroupLinkResult.Ok();
        }

        var expectedUrl = $"https://groups.google.com/a/{_options.Domain}/g/{team.GoogleGroupPrefix}";

        if (existingGroup is not null &&
            string.Equals(existingGroup.Url, expectedUrl, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Team {TeamId} already has active Group resource {ResourceId} matching prefix",
                teamId, existingGroup.Id);
            return GroupLinkResult.Ok();
        }

        var email = $"{team.GoogleGroupPrefix}@{_options.Domain}";

        var activeConflict = await FindActiveGroupByUrlAsync(expectedUrl, cancellationToken);
        if (activeConflict is not null)
        {
            if (activeConflict.TeamId == teamId)
                return GroupLinkResult.Error("This group is already linked to this team.");

            var conflictTeam = await teamService.GetTeamAsync(activeConflict.TeamId, cancellationToken);
            var conflictName = conflictTeam?.Name ?? "another team";
            return GroupLinkResult.Error($"This group is already linked to team \"{conflictName}\".");
        }

        var inactiveForTeam = await FindInactiveGroupForTeamByUrlAsync(teamId, expectedUrl, cancellationToken);
        if (inactiveForTeam is not null && !confirmReactivation)
        {
            return GroupLinkResult.NeedsConfirmation(
                "This group was previously linked to this team. Reactivate it?",
                inactiveForTeam.Id);
        }

        var now = clock.GetCurrentInstant();

        if (inactiveForTeam is not null && confirmReactivation)
        {
            await resourceRepository.ReactivateAsync(
                inactiveForTeam.Id,
                inactiveForTeam.Name,
                inactiveForTeam.Url,
                now,
                newGoogleId: null,
                newPermissionLevel: null,
                cancellationToken);

            await auditLogService.LogAsync(
                AuditAction.GoogleResourceProvisioned, nameof(GoogleResource), inactiveForTeam.Id,
                "Reactivated Google Group resource for team",
                nameof(GoogleWorkspaceSyncService),
                relatedEntityId: teamId, relatedEntityType: "Team");

            return GroupLinkResult.Ok();
        }

        if (existingGroup is not null)
        {
            await resourceRepository.DeactivateAsync(existingGroup.Id, cancellationToken);

            await auditLogService.LogAsync(
                AuditAction.GoogleResourceDeactivated, nameof(GoogleResource), existingGroup.Id,
                $"Deactivated Google Group resource (prefix changed to '{team.GoogleGroupPrefix}')",
                nameof(GoogleWorkspaceSyncService),
                relatedEntityId: teamId, relatedEntityType: "Team");
        }

        // Delegate to the central recon path. ReconcileOneAsync looks up the
        // group, auto-provisions it (with enforced settings) when missing, and
        // syncs membership. Returns a diff carrying the Google numeric id on
        // success — we use it to write the team-side GoogleResource row.
        var diff = await googleGroupSync.ReconcileOneAsync(email, SyncAction.Execute, cancellationToken, scheduleRetries: false);
        if (string.IsNullOrEmpty(diff.GoogleId))
        {
            logger.LogWarning(
                "Failed to ensure Google Group '{Email}' for team {TeamId} via recon: {Error}",
                email, teamId, diff.ErrorMessage);
            return GroupLinkResult.Error(diff.ErrorMessage ?? $"Failed to ensure Google Group {email}");
        }

        var resource = new GoogleResource
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ResourceType = GoogleResourceType.Group,
            GoogleId = diff.GoogleId,
            Name = team.Name,
            Url = expectedUrl,
            ProvisionedAt = now,
            LastSyncedAt = now,
            IsActive = true
        };

        await resourceRepository.AddAsync(resource, cancellationToken);

        await auditLogService.LogAsync(
            AuditAction.GoogleResourceProvisioned, nameof(GoogleResource), resource.Id,
            $"Linked Google Group '{team.Name}' ({email}) for team",
            nameof(GoogleWorkspaceSyncService),
            relatedEntityId: teamId, relatedEntityType: "Team");

        return GroupLinkResult.Ok();
    }

    /// <inheritdoc />
    public async Task<GroupSettingsDriftResult> CheckGroupSettingsAsync(CancellationToken cancellationToken = default)
    {
        var mode = await syncSettingsService.GetModeAsync(SyncServiceType.GoogleGroups, cancellationToken);
        if (mode == SyncMode.None)
        {
            logger.LogInformation("Google Groups sync is disabled — skipping settings drift check");
            return new GroupSettingsDriftResult
            {
                Skipped = true,
                SkipReason = "Google Groups sync mode is set to None"
            };
        }

        var groupResources = await GetActiveGroupResourcesAsync(cancellationToken);

        // Filter to groups whose team is still active. TeamInfo cache provides
        // the read-model (Name/Slug/GoogleGroupPrefix/IsActive) cross-section
        // so we never traverse the obsolete cross-section nav on GoogleResource.
        var teamsById = await teamService.GetTeamsAsync(cancellationToken);
        var filtered = groupResources
            .Where(r => teamsById.TryGetValue(r.TeamId, out var t) && t.IsActive)
            .ToList();

        logger.LogInformation("Checking group settings for {Count} active Google Groups", filtered.Count);

        var reports = new List<GroupSettingsDriftReport>();
        foreach (var resource in filtered)
        {
            var groupEmail = GoogleGroupKeyHelper.TryGetGroupKey(
                resource, teamsById[resource.TeamId].GoogleGroupEmail, _options.Domain);

            if (string.IsNullOrEmpty(groupEmail))
            {
                reports.Add(new GroupSettingsDriftReport
                {
                    ResourceId = resource.Id,
                    GroupName = resource.Name,
                    Url = resource.Url,
                    ErrorMessage = "Cannot determine group email address"
                });
                continue;
            }

            var report = await CheckSingleGroupSettingsAsync(resource, groupEmail, cancellationToken);
            reports.Add(report);
        }

        return new GroupSettingsDriftResult
        {
            Reports = reports,
            ExpectedSettings = BuildExpectedSettingsDictionary()
        };
    }

    private async Task<GroupSettingsDriftReport> CheckSingleGroupSettingsAsync(
        GoogleResource resource,
        string groupEmail,
        CancellationToken cancellationToken)
    {
        var getResult = await groupProvisioning.GetGroupSettingsAsync(groupEmail, cancellationToken);
        if (getResult.Settings is null)
        {
            var code = getResult.Error?.StatusCode ?? 0;
            if (code == 404 || code == 403)
            {
                logger.LogWarning("Cannot read settings for group '{GroupEmail}' (HTTP {Code})", groupEmail, code);
                return new GroupSettingsDriftReport
                {
                    ResourceId = resource.Id,
                    GroupEmail = groupEmail,
                    GroupName = resource.Name,
                    Url = resource.Url,
                    ErrorMessage = $"Google API error: {code} — {getResult.Error?.RawMessage}"
                };
            }
            logger.LogWarning(
                "Error fetching settings for group '{GroupEmail}' — HTTP {Code}: {Message}",
                groupEmail, code, getResult.Error?.RawMessage);
            return new GroupSettingsDriftReport
            {
                ResourceId = resource.Id,
                GroupEmail = groupEmail,
                GroupName = resource.Name,
                Url = resource.Url,
                ErrorMessage = $"Error: {getResult.Error?.RawMessage}"
            };
        }

        var drifts = new List<GroupSettingDrift>();
        var expected = BuildExpectedSettingsDictionary();
        var actualDict = SnapshotToEnforcedDict(getResult.Settings);

        foreach (var (key, expectedValue) in expected)
            CompareGroupSetting(drifts, key, expectedValue, actualDict.GetValueOrDefault(key));

        if (drifts.Count > 0)
        {
            logger.LogWarning("Group '{GroupEmail}' has {DriftCount} setting drift(s): {Drifts}",
                groupEmail, drifts.Count,
                string.Join(", ", drifts.Select(d => $"{d.SettingName}: expected={d.ExpectedValue}, actual={d.ActualValue}")));
        }

        return new GroupSettingsDriftReport
        {
            ResourceId = resource.Id,
            GroupEmail = groupEmail,
            GroupName = resource.Name,
            Url = resource.Url,
            Drifts = drifts
        };
    }

    /// <inheritdoc />
    public async Task<GroupSettingsRemediationResult> RemediateGroupSettingsAsync(string groupEmail, CancellationToken cancellationToken = default)
    {
        try
        {
            // Settings remediation is always allowed — it doesn't add/remove members.
            var error = await groupProvisioning.UpdateGroupSettingsAsync(
                groupEmail, BuildExpectedGroupSettings(), cancellationToken);

            if (error is not null)
            {
                logger.LogError(
                    "Failed to remediate settings for Google Group {GroupEmail} — HTTP {Code}: {Message}",
                    groupEmail, error.StatusCode, error.RawMessage);
                return GroupSettingsRemediationResult.Failure(
                    $"Google Groups settings update failed for {groupEmail}: HTTP {error.StatusCode} — {error.RawMessage}");
            }

            await auditLogService.LogAsync(
                AuditAction.GoogleResourceSettingsRemediated, nameof(GoogleResource), Guid.Empty,
                $"Remediated settings for Google Group '{groupEmail}'",
                nameof(GoogleWorkspaceSyncService));

            return GroupSettingsRemediationResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to remediate settings for {GroupEmail}", groupEmail);
            return GroupSettingsRemediationResult.Failure($"Remediation failed for {groupEmail}: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<AllGroupsResult> GetAllDomainGroupsAsync(CancellationToken cancellationToken = default)
    {
        var listResult = await directory.ListDomainGroupsAsync(cancellationToken);
        if (listResult.Groups is null)
        {
            logger.LogError(
                "Failed to enumerate domain groups — HTTP {Code}: {Message}",
                listResult.Error?.StatusCode, listResult.Error?.RawMessage);
            return new AllGroupsResult
            {
                ErrorMessage = listResult.Error?.RawMessage
            };
        }

        var allGroups = listResult.Groups;
        logger.LogInformation("Found {Count} Google Groups on domain {Domain}", allGroups.Count, _options.Domain);

        var teams = (await teamService.GetTeamsAsync(cancellationToken)).Values
            .Where(t => t.IsActive);
        var teamsByPrefix = teams
            .Where(t => t.GoogleGroupPrefix is not null)
            .GroupBy(t => t.GoogleGroupPrefix!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var expectedSettings = BuildExpectedSettingsDictionary();

        // Bounded concurrency for settings fetches.
        using var semaphore = new SemaphoreSlim(5);
        var tasks = allGroups
            .Where(g => !string.IsNullOrEmpty(g.Email))
            .Select(async group =>
            {
                var email = group.Email;
                var prefix = email.Split('@')[0];
                teamsByPrefix.TryGetValue(prefix, out var linkedTeam);

                string? errorMessage = null;
                var drifts = new List<GroupSettingDrift>();
                var actualSettings = new Dictionary<string, string>(StringComparer.Ordinal);

                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var getResult = await groupProvisioning.GetGroupSettingsAsync(email, cancellationToken);
                    if (getResult.Settings is null)
                    {
                        var code = getResult.Error?.StatusCode ?? 0;
                        logger.LogWarning("Cannot read settings for group '{GroupEmail}' (HTTP {Code})", email, code);
                        errorMessage = $"Google API error: {code} — {getResult.Error?.RawMessage}";
                    }
                    else
                    {
                        PopulateActualSettings(actualSettings, getResult.Settings);
                        foreach (var (key, expectedValue) in expectedSettings)
                        {
                            actualSettings.TryGetValue(key, out var actualValue);
                            CompareGroupSetting(drifts, key, expectedValue, actualValue);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning("Fetching settings for group {GroupEmail} cancelled by caller", email);
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error fetching settings for group '{GroupEmail}'", email);
                    errorMessage = $"Error: {ex.Message}";
                }
                finally
                {
                    semaphore.Release();
                }

                return new DomainGroupInfo
                {
                    GroupEmail = email,
                    DisplayName = group.DisplayName ?? email,
                    GoogleId = group.Id,
                    MemberCount = (int)(group.DirectMembersCount ?? 0),
                    LinkedTeamName = linkedTeam?.Name,
                    LinkedTeamId = linkedTeam?.Id,
                    LinkedTeamSlug = linkedTeam?.Slug,
                    ActualSettings = actualSettings,
                    Drifts = drifts,
                    ErrorMessage = errorMessage
                };
            });

        var groupInfos = (await Task.WhenAll(tasks)).ToList();

        var sorted = groupInfos
            .OrderBy(g => g.LinkedTeamId is null ? 1 : 0)
            .ThenBy(g => g.GroupEmail, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AllGroupsResult
        {
            Groups = sorted,
            ExpectedSettings = expectedSettings
        };
    }

    /// <inheritdoc />
    public async Task<int> UpdateDriveFolderPathsAsync(CancellationToken cancellationToken = default)
    {
        var driveResources = await resourceRepository.GetActiveDriveFoldersAsync(cancellationToken);

        if (driveResources.Count == 0) return 0;
        var teamsById = await teamService.GetTeamsAsync(cancellationToken);
        var filtered = driveResources
            .Where(r => teamsById.TryGetValue(r.TeamId, out var t) && t.IsActive)
            .ToList();

        if (filtered.Count == 0) return 0;

        var updatedCount = 0;
        foreach (var resource in filtered)
        {
            try
            {
                var fullPath = await ResolveDriveFolderPathAsync(resource.GoogleId, cancellationToken);
                if (fullPath is not null && !string.Equals(resource.Name, fullPath, StringComparison.Ordinal))
                {
                    logger.LogInformation(
                        "Drive folder path changed for resource {ResourceId}: '{OldName}' -> '{NewName}'",
                        resource.Id, resource.Name, fullPath);
                    await resourceRepository.UpdateNameAsync(resource.Id, fullPath, cancellationToken);
                    updatedCount++;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to resolve Drive folder path for resource {ResourceId} ({GoogleId})",
                    resource.Id, resource.GoogleId);
            }
        }

        return updatedCount;
    }

    /// <summary>
    /// Resolves the full path of a Drive folder by walking the parent chain.
    /// </summary>
    private async Task<string?> ResolveDriveFolderPathAsync(string fileId, CancellationToken cancellationToken)
    {
        var segments = new List<string>();
        var currentId = fileId;
        const int maxDepth = 20;

        for (var depth = 0; depth < maxDepth; depth++)
        {
            var fileResult = await drivePermissions.GetFileAsync(currentId, cancellationToken);
            if (fileResult.File is null)
            {
                var code = fileResult.Error?.StatusCode ?? 0;
                if (code == 404)
                {
                    logger.LogWarning("Drive folder {GoogleId} not found", currentId);
                }
                break;
            }

            var file = fileResult.File;

            // If the file IS the Shared Drive root, fetch the drive name.
            if (!string.IsNullOrEmpty(file.DriveId)
                && string.Equals(currentId, file.DriveId, StringComparison.Ordinal))
            {
                var driveResult = await drivePermissions.GetSharedDriveAsync(file.DriveId, cancellationToken);
                if (driveResult.Drive is not null)
                {
                    segments.Add(driveResult.Drive.Name);
                }
                else
                {
                    segments.Add(file.Name ?? string.Empty);
                }
                break;
            }

            segments.Add(file.Name ?? string.Empty);

            if (file.Parents is null || file.Parents.Count == 0)
                break;

            currentId = file.Parents[0];
        }

        if (segments.Count == 0)
            return null;

        segments.Reverse();
        return string.Join(" / ", segments);
    }

    /// <inheritdoc />
    public async Task SetInheritedPermissionsDisabledAsync(
        string googleFileId,
        bool restrict,
        CancellationToken cancellationToken = default)
    {
        var error = await drivePermissions.SetInheritedPermissionsDisabledAsync(googleFileId, restrict, cancellationToken);
        if (error is not null)
        {
            logger.LogWarning(
                "Failed to set inheritedPermissionsDisabled={Restrict} on {FileId} — HTTP {Code}: {Message}",
                restrict, googleFileId, error.StatusCode, error.RawMessage);
            throw new InvalidOperationException(
                $"Google Drive inheritedPermissionsDisabled update failed for {googleFileId}: HTTP {error.StatusCode} — {error.RawMessage}");
        }
    }

    /// <inheritdoc />
    public async Task<int> EnforceInheritedAccessRestrictionsAsync(CancellationToken cancellationToken = default)
    {
        var driveResources = await resourceRepository.GetActiveDriveFoldersAsync(cancellationToken);

        if (driveResources.Count == 0) return 0;
        var teamsById = await teamService.GetTeamsAsync(cancellationToken);

        var restricted = driveResources
            .Where(r => r.RestrictInheritedAccess
                && r.ResourceType == GoogleResourceType.DriveFolder
                && teamsById.TryGetValue(r.TeamId, out var t) && t.IsActive)
            .ToList();

        if (restricted.Count == 0) return 0;

        var correctedCount = 0;
        foreach (var resource in restricted)
        {
            try
            {
                var fileResult = await drivePermissions.GetFileAsync(resource.GoogleId, cancellationToken);
                if (fileResult.File is null)
                {
                    if ((fileResult.Error?.StatusCode ?? 0) == 404)
                    {
                        logger.LogWarning(
                            "Drive folder {GoogleId} not found (resource {ResourceId}) during inherited access check — may have been deleted",
                            resource.GoogleId, resource.Id);
                    }
                    else
                    {
                        logger.LogWarning(
                            "Failed to fetch file {GoogleId} during inherited access check — HTTP {Code}: {Message}",
                            resource.GoogleId, fileResult.Error?.StatusCode, fileResult.Error?.RawMessage);
                    }
                    continue;
                }

                if (fileResult.File.InheritedPermissionsDisabled != true)
                {
                    logger.LogWarning(
                        "Inherited access drift detected for resource {ResourceId} ({GoogleId}): " +
                        "inheritedPermissionsDisabled is {Actual}, expected true. Correcting.",
                        resource.Id, resource.GoogleId, fileResult.File.InheritedPermissionsDisabled);

                    await SetInheritedPermissionsDisabledAsync(resource.GoogleId, true, cancellationToken);

                    await auditLogService.LogAsync(
                        AuditAction.GoogleResourceInheritanceDriftCorrected,
                        nameof(GoogleResource), resource.Id,
                        $"Corrected inherited access drift for Drive folder '{resource.Name}' — re-disabled inherited permissions",
                        "GoogleResourceReconciliationJob");

                    correctedCount++;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to check/enforce inherited access restriction for resource {ResourceId} ({GoogleId})",
                    resource.Id, resource.GoogleId);
            }
        }

        return correctedCount;
    }

    /// <inheritdoc />
    public Task<int> GetFailedSyncEventCountAsync(CancellationToken cancellationToken = default)
        => googleSyncOutboxRepository.CountFailedAsync(cancellationToken);

    /// <inheritdoc />
    public Task<int> GetPendingSyncEventCountAsync(CancellationToken cancellationToken = default)
        => googleSyncOutboxRepository.CountPendingAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<GoogleSyncOutboxEventSnapshot>> GetRecentOutboxEventsAsync(
        int take, CancellationToken cancellationToken = default)
    {
        var events = await googleSyncOutboxRepository.GetRecentAsync(take, cancellationToken);
        return events
            .Select(e => new GoogleSyncOutboxEventSnapshot(
                e.Id,
                e.EventType,
                e.TeamId,
                e.UserId,
                e.OccurredAt,
                e.ProcessedAt,
                e.RetryCount,
                e.LastError,
                e.FailedPermanently))
            .ToList();
    }

    /// <inheritdoc />
    public Task<bool> RequeueOutboxEventAsync(Guid id, CancellationToken cancellationToken = default)
        => googleSyncOutboxRepository.RequeueAsync(id, cancellationToken);

    /// <inheritdoc />
    public Task<int> RequeueAllFailedOutboxEventsAsync(CancellationToken cancellationToken = default)
        => googleSyncOutboxRepository.RequeueAllFailedAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<int> EnqueueUserSyncAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await userService.GetUserInfoAsync(userId, cancellationToken);

        // If the user's GoogleEmailStatus is Rejected, AddUserToTeamResourcesAsync will
        // silently skip every enqueued event — don't enqueue at all (nobodies-collective/Humans#847).
        if (user?.GoogleEmailStatus == GoogleEmailStatus.Rejected)
        {
            logger.LogDebug("EnqueueUserSyncAsync skipped for user {UserId} — GoogleEmailStatus is Rejected", userId);
            return 0;
        }

        var now = clock.GetCurrentInstant();
        var memberships = await teamService.GetUserTeamMembershipsAsync(userId, cancellationToken);
        var events = memberships
            .Select(t => new GoogleSyncOutboxEvent
            {
                Id = Guid.NewGuid(),
                EventType = GoogleSyncOutboxEventTypes.AddUserToTeamResources,
                TeamId = t.TeamId,
                UserId = userId,
                OccurredAt = now,
                DeduplicationKey = $"admin-resync:{userId}:{t.TeamId}:{now.ToUnixTimeTicks()}"
            })
            .ToList();

        if (events.Count > 0)
            await googleSyncOutboxRepository.AddRangeAsync(events, cancellationToken);

        return events.Count;
    }

    private async Task<DrivePermissionLevel> ResolvePermissionLevelForUserAsync(
        string googleId, Guid userId, CancellationToken ct)
    {
        var claims = await GoogleDriveAccessSyncService.LoadClaimsAsync(driveAccessSources, googleId, logger, ct);
        var claim = claims.SingleOrDefault(c => string.Equals(c.FolderId, googleId, StringComparison.OrdinalIgnoreCase));
        if (claim?.IsCollision == true)
        {
            var error = $"Google Drive access source collision for {googleId}: {string.Join(", ", claim.SourceNames)}";
            logger.LogError("{Error}", error);
            await auditLogService.LogAsync(AuditAction.AnomalousPermissionDetected,
                GoogleResourceType.DriveFolder.ToString(), Guid.Empty, error, nameof(GoogleWorkspaceSyncService));
            return DrivePermissionLevel.None;
        }
        var level = claim?.Access.GetValueOrDefault(userId) ?? DrivePermissionLevel.None;
        if (level == DrivePermissionLevel.None)
            logger.LogWarning("Skipping Drive grant for {UserId} on {GoogleId}: no source claims the user's access", userId, googleId);
        return level;
    }

    /// <summary>
    /// Looks up an active Google Group resource by the given web URL, regardless
    /// of the owning team. Used by EnsureTeamGroup to detect cross-team conflicts.
    /// </summary>
    private async Task<GoogleResource?> FindActiveGroupByUrlAsync(string expectedUrl, CancellationToken ct)
    {
        // The repository doesn't expose URL-based lookup, so scan active groups.
        // The set is small (dozens of groups at most).
        var all = await GetActiveGroupResourcesAsync(ct);
        return all.FirstOrDefault(r => r.Url is not null &&
            string.Equals(r.Url, expectedUrl, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Looks up an inactive Google Group resource for the given team by URL.
    /// Used by EnsureTeamGroup's reactivation path.
    /// </summary>
    private async Task<GoogleResource?> FindInactiveGroupForTeamByUrlAsync(
        Guid teamId, string expectedUrl, CancellationToken ct)
    {
        var parts = expectedUrl.Split("/g/");
        if (parts.Length != 2) return null;
        var prefix = parts[1].Split('/')[0];
        var beforeG = parts[0];
        var aIdx = beforeG.LastIndexOf("/a/", StringComparison.Ordinal);
        if (aIdx < 0) return null;
        var domain = beforeG[(aIdx + 3)..];
        var email = string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(domain)
            ? null : $"{prefix}@{domain}";
        if (email is null) return null;
        return await resourceRepository.FindInactiveGroupByCandidatesAsync(
            teamId,
            googleNumericId: email,
            normalizedGroupEmail: email,
            ct);
    }

    /// <summary>
    /// Returns every active Group resource across all teams. Used by the
    /// domain-wide drift-check and by the cross-team conflict check in
    /// EnsureTeamGroup. At our scale this is cheap.
    /// </summary>
    private async Task<IReadOnlyList<GoogleResource>> GetActiveGroupResourcesAsync(CancellationToken ct)
    {
        var allCounts = await resourceRepository.GetActiveResourceCountsByTeamAsync(ct);
        var teamIds = allCounts.Keys.ToList();
        if (teamIds.Count == 0)
            return [];

        var perTeam = await resourceRepository.GetActiveByTeamIdsAsync(teamIds, ct);
        return perTeam.Values
            .SelectMany(rs => rs)
            .Where(r => r.ResourceType == GoogleResourceType.Group && r.IsActive)
            .ToList();
    }

    private GroupSettingsExpected BuildExpectedGroupSettings() =>
        GroupSettingsPolicy.BuildExpected(_options.Groups);

    private Dictionary<string, string> BuildExpectedSettingsDictionary()
    {
        var e = BuildExpectedGroupSettings();
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WhoCanJoin"] = e.WhoCanJoin!,
            ["WhoCanViewMembership"] = e.WhoCanViewMembership!,
            ["WhoCanContactOwner"] = e.WhoCanContactOwner!,
            ["WhoCanPostMessage"] = e.WhoCanPostMessage!,
            ["WhoCanViewGroup"] = e.WhoCanViewGroup!,
            ["WhoCanModerateMembers"] = e.WhoCanModerateMembers!,
            ["AllowExternalMembers"] = e.AllowExternalMembers ? "true" : "false",
            ["IsArchived"] = e.IsArchived ? "true" : "false",
            ["MembersCanPostAsTheGroup"] = e.MembersCanPostAsTheGroup ? "true" : "false",
            ["IncludeInGlobalAddressList"] = e.IncludeInGlobalAddressList ? "true" : "false",
            ["AllowWebPosting"] = e.AllowWebPosting ? "true" : "false",
            ["MessageModerationLevel"] = e.MessageModerationLevel,
            ["SpamModerationLevel"] = e.SpamModerationLevel,
            ["EnableCollaborativeInbox"] = e.EnableCollaborativeInbox ? "true" : "false"
        };
    }

    /// <summary>
    /// Projects a <see cref="GroupSettingsSnapshot"/> to the enforced-settings
    /// dictionary used for drift comparison.
    /// </summary>
    private static Dictionary<string, string?> SnapshotToEnforcedDict(GroupSettingsSnapshot s) => new(StringComparer.Ordinal)
    {
        ["WhoCanJoin"] = s.WhoCanJoin,
        ["WhoCanViewMembership"] = s.WhoCanViewMembership,
        ["WhoCanContactOwner"] = s.WhoCanContactOwner,
        ["WhoCanPostMessage"] = s.WhoCanPostMessage,
        ["WhoCanViewGroup"] = s.WhoCanViewGroup,
        ["WhoCanModerateMembers"] = s.WhoCanModerateMembers,
        ["AllowExternalMembers"] = s.AllowExternalMembers,
        ["IsArchived"] = s.IsArchived,
        ["MembersCanPostAsTheGroup"] = s.MembersCanPostAsTheGroup,
        ["IncludeInGlobalAddressList"] = s.IncludeInGlobalAddressList,
        ["AllowWebPosting"] = s.AllowWebPosting,
        ["MessageModerationLevel"] = s.MessageModerationLevel,
        ["SpamModerationLevel"] = s.SpamModerationLevel,
        ["EnableCollaborativeInbox"] = s.EnableCollaborativeInbox
    };

    /// <summary>
    /// Populates the full actual-settings dictionary (enforced + deprecated)
    /// for the domain-wide "all groups" page — deprecated settings stay visible
    /// to admins.
    /// </summary>
    private static void PopulateActualSettings(Dictionary<string, string> dict, GroupSettingsSnapshot s)
    {
        void Add(string key, string? val) { if (val is not null) dict[key] = val; }

        Add("WhoCanJoin", s.WhoCanJoin);
        Add("WhoCanViewMembership", s.WhoCanViewMembership);
        Add("WhoCanContactOwner", s.WhoCanContactOwner);
        Add("WhoCanPostMessage", s.WhoCanPostMessage);
        Add("WhoCanViewGroup", s.WhoCanViewGroup);
        Add("WhoCanModerateMembers", s.WhoCanModerateMembers);
        Add("WhoCanModerateContent", s.WhoCanModerateContent);
        Add("WhoCanAssistContent", s.WhoCanAssistContent);
        Add("WhoCanDiscoverGroup", s.WhoCanDiscoverGroup);
        Add("WhoCanLeaveGroup", s.WhoCanLeaveGroup);
        Add("AllowExternalMembers", s.AllowExternalMembers);
        Add("AllowWebPosting", s.AllowWebPosting);
        Add("IsArchived", s.IsArchived);
        Add("ArchiveOnly", s.ArchiveOnly);
        Add("MembersCanPostAsTheGroup", s.MembersCanPostAsTheGroup);
        Add("IncludeInGlobalAddressList", s.IncludeInGlobalAddressList);
        Add("EnableCollaborativeInbox", s.EnableCollaborativeInbox);
        Add("MessageModerationLevel", s.MessageModerationLevel);
        Add("SpamModerationLevel", s.SpamModerationLevel);
        Add("ReplyTo", s.ReplyTo);
        Add("CustomReplyTo", s.CustomReplyTo);
        Add("IncludeCustomFooter", s.IncludeCustomFooter);
        Add("CustomFooterText", s.CustomFooterText);
        Add("SendMessageDenyNotification", s.SendMessageDenyNotification);
        Add("DefaultMessageDenyNotificationText", s.DefaultMessageDenyNotificationText);
        Add("FavoriteRepliesOnTop", s.FavoriteRepliesOnTop);
        Add("DefaultSender", s.DefaultSender);
        Add("PrimaryLanguage", s.PrimaryLanguage);

        Add("WhoCanInvite", s.WhoCanInvite);
        Add("WhoCanAdd", s.WhoCanAdd);
        Add("ShowInGroupDirectory", s.ShowInGroupDirectory);
        Add("AllowGoogleCommunication", s.AllowGoogleCommunication);
        Add("WhoCanApproveMembers", s.WhoCanApproveMembers);
        Add("WhoCanBanUsers", s.WhoCanBanUsers);
        Add("WhoCanModifyMembers", s.WhoCanModifyMembers);
        Add("WhoCanApproveMessages", s.WhoCanApproveMessages);
        Add("WhoCanDeleteAnyPost", s.WhoCanDeleteAnyPost);
        Add("WhoCanDeleteTopics", s.WhoCanDeleteTopics);
        Add("WhoCanLockTopics", s.WhoCanLockTopics);
        Add("WhoCanMoveTopicsIn", s.WhoCanMoveTopicsIn);
        Add("WhoCanMoveTopicsOut", s.WhoCanMoveTopicsOut);
        Add("WhoCanPostAnnouncements", s.WhoCanPostAnnouncements);
        Add("WhoCanHideAbuse", s.WhoCanHideAbuse);
        Add("WhoCanMakeTopicsSticky", s.WhoCanMakeTopicsSticky);
        Add("WhoCanAssignTopics", s.WhoCanAssignTopics);
        Add("WhoCanUnassignTopic", s.WhoCanUnassignTopic);
        Add("WhoCanTakeTopics", s.WhoCanTakeTopics);
        Add("WhoCanMarkDuplicate", s.WhoCanMarkDuplicate);
        Add("WhoCanMarkNoResponseNeeded", s.WhoCanMarkNoResponseNeeded);
        Add("WhoCanMarkFavoriteReplyOnAnyTopic", s.WhoCanMarkFavoriteReplyOnAnyTopic);
        Add("WhoCanMarkFavoriteReplyOnOwnTopic", s.WhoCanMarkFavoriteReplyOnOwnTopic);
        Add("WhoCanUnmarkFavoriteReplyOnAnyTopic", s.WhoCanUnmarkFavoriteReplyOnAnyTopic);
        Add("WhoCanEnterFreeFormTags", s.WhoCanEnterFreeFormTags);
        Add("WhoCanModifyTagsAndCategories", s.WhoCanModifyTagsAndCategories);
        Add("WhoCanAddReferences", s.WhoCanAddReferences);
        Add("MessageDisplayFont", s.MessageDisplayFont);
        Add("MaxMessageBytes", s.MaxMessageBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void CompareGroupSetting(
        List<GroupSettingDrift> drifts,
        string settingName,
        string expectedValue,
        string? actualValue)
    {
        if (actualValue is null) return;
        if (!string.Equals(expectedValue, actualValue, StringComparison.OrdinalIgnoreCase))
        {
            drifts.Add(new GroupSettingDrift(settingName, expectedValue, actualValue));
        }
    }
}
