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

/// <summary>Group membership reconciliation lives in <see cref="IGoogleGroupSync"/>.</summary>
internal sealed class GoogleWorkspaceSyncService(
    IGoogleGroupProvisioningClient groupProvisioning,
    IGoogleDrivePermissionsClient drivePermissions,
    IGoogleDirectoryClient directory,
    IGoogleResourceRepository resourceRepository,
    IGoogleSyncOutboxRepository googleSyncOutboxRepository,
    ITeamService teamService,
    IUserService userService,
    IWorkspaceIdentityManagement workspaceIdentities,
    IGoogleGroupSync googleGroupSync,
    IGoogleDriveSync googleDriveSync,
    IEnumerable<IGoogleDriveAccessSource> driveAccessSources,
    IAuditLogService auditLogService,
    IGoogleSyncLogService googleSyncLog,
    ISyncSettingsService syncSettingsService,
    IGoogleDriveAccessSyncScheduler driveAccessSyncScheduler,
    IOptions<GoogleWorkspaceOptions> options,
    IClock clock,
    ILogger<GoogleWorkspaceSyncService> logger) : IGoogleSyncManagement
{
    private readonly GoogleWorkspaceOptions _options = options.Value;


    /// <summary>GATEWAY: only path that adds a user to a Drive resource. Skips when GoogleDrive mode is None. <paramref name="permissionLevelOverride"/>: resolved max across teams sharing the resource; null = use resource's level.</summary>
    private async Task<GoogleResourceGrantResult> AddUserToDriveAsync(
        GoogleResource resource,
        string userEmail,
        Guid userId,
        string identityEmail,
        DrivePermissionLevel? permissionLevelOverride,
        GoogleSyncSource syncSource,
        CancellationToken cancellationToken)
    {
        var mode = await syncSettingsService.GetModeAsync(SyncServiceType.GoogleDrive, cancellationToken);
        if (mode == SyncMode.None)
        {
            logger.LogDebug("Skipping AddUserToDrive — GoogleDrive sync mode is None");
            return new GoogleResourceGrantResult(GoogleResourceGrantOutcome.Deferred, userId, identityEmail);
        }

        var effectiveLevel = permissionLevelOverride ?? resource.DrivePermissionLevel;
        var apiRole = effectiveLevel.ToApiRole();

        // Drive rejects a plus-addressed local part with an opaque 400. Plus addresses are only equivalent on Gmail
        // (elsewhere the base may be a different identity), so the base is tried only after Drive refuses.
        var driveTargetEmail = DrivePlusAddressFallback.Apply(
            await workspaceIdentities.GetDriveEmailsAsync(cancellationToken),
            userId, identityEmail, EmailNormalization.CanonicalizeGmail(userEmail));

        var (result, grantedEmail) = await DrivePlusAddressFallback.CreateAsync(
            email => drivePermissions.CreatePermissionAsync(resource.GoogleId, email, apiRole, cancellationToken),
            driveTargetEmail, userId, identityEmail, workspaceIdentities, cancellationToken);

        var insteadOf = string.Equals(grantedEmail, driveTargetEmail, StringComparison.Ordinal)
            ? ""
            : $" in place of {driveTargetEmail}, which Drive refused";
        switch (result.Outcome)
        {
            // A fallback onto a base address that already had access still changes the
            // member's Drive address, so it is logged like a grant.
            case DrivePermissionCreateOutcome.Created:
            case DrivePermissionCreateOutcome.AlreadyExists when insteadOf.Length > 0:
                await googleSyncLog.LogAsync(
                    GoogleSyncLogAction.AccessGranted, resource.Id,
                    $"Granted Drive access ({effectiveLevel}) to {grantedEmail}{insteadOf} ({resource.Name})",
                    nameof(GoogleWorkspaceSyncService),
                    grantedEmail, apiRole, syncSource, success: true,
                    userId: userId, ct: cancellationToken);
                return new GoogleResourceGrantResult(GoogleResourceGrantOutcome.Accepted, userId, identityEmail);

            case DrivePermissionCreateOutcome.AlreadyExists:
                logger.LogDebug("Permission already exists for {Email} on {GoogleId}", userEmail, resource.GoogleId);
                return new GoogleResourceGrantResult(GoogleResourceGrantOutcome.Accepted, userId, identityEmail);

            case DrivePermissionCreateOutcome.Failed:
                logger.LogWarning(
                    "Google API error granting {Role} to {Email} on {GoogleId} — HTTP {Code}: {Message}",
                    apiRole, userEmail, resource.GoogleId,
                    result.Error?.StatusCode, result.Error?.RawMessage);
                // Recorded so the failed grant shows on the monitor pages and in the GDPR export, like group failures.
                await googleSyncLog.LogAsync(
                    GoogleSyncLogAction.AccessGranted, resource.Id,
                    $"Failed to grant Drive access ({effectiveLevel}) to {userEmail} ({resource.Name}): " +
                    $"HTTP {result.Error?.StatusCode} — {result.Error?.RawMessage}",
                    nameof(GoogleWorkspaceSyncService),
                    userEmail, apiRole, syncSource, success: false,
                    errorMessage: result.Error?.RawMessage,
                    userId: userId, ct: cancellationToken);
                await HandleDriveAddFailureAsync(resource, userEmail, userId, identityEmail, result.Error, cancellationToken);
                return new GoogleResourceGrantResult(GoogleResourceGrantOutcome.Failed, userId, identityEmail,
                    Retryable: !DrivePermissionErrorPolicy.IsPermanentFailure(result.Error?.StatusCode),
                    ErrorMessage: $"{resource.Name}: HTTP {result.Error?.StatusCode} — {result.Error?.RawMessage}");
        }
        throw new InvalidOperationException($"Unknown Drive permission outcome {result.Outcome}");
    }

    /// <summary>
    /// A Drive target rejection marks the owner's identity <see cref="GoogleEmailStatus.Rejected"/>, so retries stop
    /// until an admin clears it. Mirrors <c>GoogleGroupSyncService.HandleGroupAddFailureAsync</c>.
    /// </summary>
    private async Task HandleDriveAddFailureAsync(
        GoogleResource resource,
        string userEmail,
        Guid userId,
        string identityEmail,
        GoogleClientError? error,
        CancellationToken ct)
    {
        var statusCode = error?.StatusCode ?? 0;
        var rawMessage = error?.RawMessage ?? string.Empty;

        // 400: no Google account on the recipient's domain; 403: "Cannot share a shared drive with non Google
        // accounts". Other 403s are caller-permission failures; the phrase predicate below tells them apart.
        if (statusCode is not (400 or 403))
        {
            return;
        }

        // Drive-specific predicate only: generic sharing-policy phrases must not flip GoogleEmailStatus.
        if (error?.IsDriveTargetRejection != true)
        {
            return;
        }

        await workspaceIdentities.SetStatusFromSyncAsync(
            userId, identityEmail, GoogleEmailStatus.Rejected, ct);

        logger.LogWarning(
            "Google rejected target email {Email} while granting Drive permission on {GoogleId} - HTTP {StatusCode}. " +
            "Google email status marked Rejected for the address. Google error: {ErrorMessage}",
            userEmail,
            resource.GoogleId,
            statusCode,
            rawMessage);
    }

    /// <inheritdoc />
    public async Task<GoogleResourceGrantResult> AddUserToTeamResourcesAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default,
        GoogleSyncSource syncSource = GoogleSyncSource.ManualSync)
    {
        // An event enqueued before a merge may name a folded-away account; Users resolves it forward. One snapshot
        // supplies target and state: a second read could see a different selection mid-edit.
        var user = await userService.GetUserInfoAsync(userId, cancellationToken);
        var target = user is null ? null : await workspaceIdentities.GetWorkspaceIdentityAsync(user.Id, cancellationToken);
        if (target is null)
        {
            logger.LogWarning("Skipped Google provisioning for user {UserId} on team {TeamId}: no eligible Google email", userId, teamId);
            return new GoogleResourceGrantResult(GoogleResourceGrantOutcome.Failed, user?.Id ?? userId,
                ErrorMessage: "No eligible Google email for provisioning");
        }
        // An old outbox event must not restore access excluded by reconciliation.
        if (!GoogleSyncMemberHydration.CanReceiveAccess(user!, target))
        {
            logger.LogDebug("Skipping AddUserToTeamResources for user {UserId} — signup rejected, suspended, deletion pending, or Google email rejected", userId);
            return new GoogleResourceGrantResult(GoogleResourceGrantOutcome.Failed, user!.Id, target.Email,
                ErrorMessage: "Member is ineligible for Google access: signup rejected, suspended, deletion pending, or email rejected");
        }
        var googleEmail = target.Email;

        // From here on the id is the resolved human's, as the email above already is: the
        // team_members rows the permission level is read from moved to the survivor at
        // merge, and the grant is audited against the account that actually holds it.
        userId = user!.Id;

        var team = await teamService.GetTeamAsync(teamId, cancellationToken);

        var outcomes = new List<GoogleResourceGrantResult>();
        await GrantTeamResourcesAsync(teamId, team?.GoogleGroupEmail);

        // Subteam member rollup: also add to parent department resources.
        if (team?.ParentTeamId is not null)
        {
            var parentTeam = await teamService.GetTeamAsync(team.ParentTeamId.Value, cancellationToken);
            await GrantTeamResourcesAsync(team.ParentTeamId.Value, parentTeam?.GoogleGroupEmail);
        }
        var failures = outcomes.Where(r => r.Outcome == GoogleResourceGrantOutcome.Failed).ToList();
        if (failures.Count > 0)
            return new GoogleResourceGrantResult(GoogleResourceGrantOutcome.Failed, userId, target.Email,
                Retryable: failures.Any(r => r.Retryable),
                ErrorMessage: string.Join("; ", failures.Select(r => r.ErrorMessage)));
        return new GoogleResourceGrantResult(
            outcomes.Any(r => r.Outcome == GoogleResourceGrantOutcome.Accepted)
                ? GoogleResourceGrantOutcome.Accepted : GoogleResourceGrantOutcome.Deferred,
            userId, target.Email);

        async Task GrantTeamResourcesAsync(Guid resourceTeamId, string? groupEmail)
        {
            foreach (var resource in await resourceRepository.GetActiveByTeamIdAsync(resourceTeamId, cancellationToken))
            {
                if (resource.ResourceType == GoogleResourceType.Group)
                {
                    // Group membership reconciliation owned by IGoogleGroupSync; this path handles Drive only.
                    await RequestGoogleGroupSyncAsync(resource, groupEmail, cancellationToken, syncSource);
                    continue;
                }

                var level = await ResolvePermissionLevelForUserAsync(
                    resource.GoogleId, userId, cancellationToken);
                if (level != DrivePermissionLevel.None)
                    outcomes.Add(await AddUserToDriveAsync(resource, googleEmail, userId, target.Email, level, syncSource, cancellationToken));
            }
        }
    }


    private async Task RequestGoogleGroupSyncAsync(
        GoogleResource resource,
        string? teamGoogleGroupEmail,
        CancellationToken cancellationToken,
        GoogleSyncSource syncSource)
    {
        var groupKey = GoogleGroupKeyHelper.TryGetGroupKey(resource, teamGoogleGroupEmail, _options.Domain);
        if (groupKey is null)
        {
            logger.LogWarning(
                "Cannot request Google Group membership sync for resource {ResourceId}: group email could not be derived",
                resource.Id);
            return;
        }

        await googleGroupSync.RequestSyncAsync(groupKey, cancellationToken, syncSource);
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
            : await googleGroupSync.ReconcileOneAsync(groupKey, action, cancellationToken, syncSource: GoogleSyncSource.ManualSync);
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
    public async Task<bool> IsFolderReachableAsync(string folderId, CancellationToken cancellationToken = default)
    {
        var result = await drivePermissions.GetFileAsync(folderId, cancellationToken);
        if (result.Error is { } error)
        {
            logger.LogWarning("Drive folder {FolderId} is unreachable (HTTP {StatusCode}): {Message}",
                folderId, error.StatusCode, error.RawMessage);
        }
        return result.File is not null;
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
                    GoogleIntegrationAuditActions.GoogleResourceDeactivated, nameof(GoogleResource), existingGroup.Id,
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
                GoogleIntegrationAuditActions.GoogleResourceProvisioned, nameof(GoogleResource), inactiveForTeam.Id,
                "Reactivated Google Group resource for team",
                nameof(GoogleWorkspaceSyncService),
                relatedEntityId: teamId, relatedEntityType: "Team");

            return GroupLinkResult.Ok();
        }

        if (existingGroup is not null)
        {
            await resourceRepository.DeactivateAsync(existingGroup.Id, cancellationToken);

            await auditLogService.LogAsync(
                GoogleIntegrationAuditActions.GoogleResourceDeactivated, nameof(GoogleResource), existingGroup.Id,
                $"Deactivated Google Group resource (prefix changed to '{team.GoogleGroupPrefix}')",
                nameof(GoogleWorkspaceSyncService),
                relatedEntityId: teamId, relatedEntityType: "Team");
        }

        // ReconcileOneAsync auto-provisions a missing group and syncs membership; its diff carries the numeric id.
        var diff = await googleGroupSync.ReconcileOneAsync(email, SyncAction.Execute, cancellationToken, scheduleRetries: false, syncSource: GoogleSyncSource.ManualSync);
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
            GoogleIntegrationAuditActions.GoogleResourceProvisioned, nameof(GoogleResource), resource.Id,
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
                GoogleIntegrationAuditActions.GoogleResourceSettingsRemediated, nameof(GoogleResource), Guid.Empty,
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

        return new AllGroupsResult
        {
            Groups = groupInfos,
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
                var fullPath = await DriveFolderPath.ResolveAsync(drivePermissions, resource.GoogleId, logger, cancellationToken);
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
                        GoogleIntegrationAuditActions.GoogleResourceInheritanceDriftCorrected,
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
    public async Task<bool> RequeueOutboxEventAsync(Guid id, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var requeued = await googleSyncOutboxRepository.RequeueAsync(id, cancellationToken);
        if (requeued)
            await AuditOutboxRequeueAsync(auditLogService, id, actorUserId);
        return requeued;
    }

    /// <inheritdoc />
    public async Task<int> RequeueAllFailedOutboxEventsAsync(Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var count = await googleSyncOutboxRepository.RequeueAllFailedAsync(cancellationToken);
        await AuditRequeueAllAsync(auditLogService, count, actorUserId);
        return count;
    }

    /// <inheritdoc />
    public async Task<int> EnqueueUserSyncAsync(Guid userId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var count = await EnqueueUserSyncEventsAsync(userId, cancellationToken);
        await AuditUserSyncRerunAsync(auditLogService, userId, count, actorUserId);
        return count;
    }

    // Admin retry audits, shared with StubGoogleSyncService so both modes leave the same trail.
    internal static Task AuditOutboxRequeueAsync(IAuditLogService auditLog, Guid id, Guid actorUserId) =>
        auditLog.LogAsync(
            GoogleIntegrationAuditActions.GoogleSyncRetryScheduled,
            "GoogleSyncOutboxEvent",
            id,
            $"Admin manually requeued Google sync outbox event {id}",
            actorUserId);

    internal static Task AuditRequeueAllAsync(IAuditLogService auditLog, int count, Guid actorUserId) =>
        auditLog.LogAsync(
            GoogleIntegrationAuditActions.GoogleSyncRetryScheduled,
            "GoogleSyncOutbox",
            Guid.Empty,
            $"Admin requeued all {count} permanently-failed Google sync outbox event(s)",
            actorUserId);

    internal static Task AuditUserSyncRerunAsync(IAuditLogService auditLog, Guid userId, int count, Guid actorUserId) =>
        auditLog.LogAsync(
            GoogleIntegrationAuditActions.GoogleSyncRetryScheduled,
            "User",
            userId,
            $"Admin enqueued Google sync re-run for user {userId} ({count} team event(s))",
            actorUserId);

    private async Task<int> EnqueueUserSyncEventsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var identity = await workspaceIdentities.GetWorkspaceIdentityAsync(userId, cancellationToken);

        // AddUserToTeamResourcesAsync would skip every event for a Rejected identity, so enqueue none.
        if (identity?.Status == GoogleEmailStatus.Rejected)
        {
            logger.LogDebug("EnqueueUserSyncAsync skipped for user {UserId} — Google identity is Rejected", userId);
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
        // A failing source throws out of here: the outbox retries the event, whereas a partial
        // roster would read as "no claim" and silently drop the new member's grant.
        var load = await GoogleDriveAccessSyncService.LoadClaimsAsync(
            driveAccessSources, googleId, logger, ct, rethrowSourceFailures: true);
        var claim = load.Claims.SingleOrDefault(c => string.Equals(c.FolderId, googleId, StringComparison.OrdinalIgnoreCase));
        var level = claim?.Access.GetValueOrDefault(userId) ?? DrivePermissionLevel.None;
        if (level == DrivePermissionLevel.None)
            logger.LogWarning("Skipping Drive grant for {UserId} on {GoogleId}: no source claims the user's access", userId, googleId);
        return level;
    }

    /// <summary>Any team's, for the cross-team conflict check.</summary>
    private async Task<GoogleResource?> FindActiveGroupByUrlAsync(string expectedUrl, CancellationToken ct)
    {
        // No URL lookup in the repository; the set is a few dozen groups.
        var all = await GetActiveGroupResourcesAsync(ct);
        return all.FirstOrDefault(r => r.Url is not null &&
            string.Equals(r.Url, expectedUrl, StringComparison.OrdinalIgnoreCase));
    }

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

    /// <summary>Includes deprecated settings, which stay visible to admins.</summary>
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
