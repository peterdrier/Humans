using Humans.Google.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Users.Contracts;
using Humans.Teams.Contracts;
using Humans.Base.Enums;
using Humans.Base.Helpers;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Google.Data;
using Humans.Google.Services.Workspace;

namespace Humans.Google.Services;

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
    private readonly GoogleDriveFolderMaintenance folderMaintenance = new(
        resourceRepository, teamService, drivePermissions, auditLogService, logger);
    private readonly GoogleSyncOutboxAdministration outboxAdministration = new(
        googleSyncOutboxRepository, workspaceIdentities, teamService, clock, auditLogService, logger);
    private readonly GoogleTeamGroupLinking groupLinking = new(
        resourceRepository, teamService, googleGroupSync, auditLogService, options.Value, clock, logger);
    private readonly GoogleGroupSettings groupSettings = new(
        groupProvisioning, directory, resourceRepository, teamService, syncSettingsService, auditLogService, options.Value, logger);


    /// <summary>GATEWAY: only path that adds a user to a Drive resource. Skips when GoogleDrive mode is None. <paramref name="permissionLevelOverride"/>: resolved max across teams sharing the resource; null = use resource's level.</summary>
    private async Task<GoogleResourceGrantResult> AddUserToDriveAsync(
        TeamGoogleResource resource,
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
        TeamGoogleResource resource,
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
        TeamGoogleResource resource,
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
        TeamGoogleResource resource,
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
    public Task<GroupLinkResult> EnsureTeamGroupAsync(
        Guid teamId,
        bool confirmReactivation = false,
        CancellationToken cancellationToken = default) =>
        groupLinking.EnsureTeamGroupAsync(teamId, confirmReactivation, cancellationToken);

    public Task<GroupSettingsDriftResult> CheckGroupSettingsAsync(CancellationToken cancellationToken = default) =>
        groupSettings.CheckGroupSettingsAsync(cancellationToken);

    public Task<GroupSettingsRemediationResult> RemediateGroupSettingsAsync(string groupEmail, CancellationToken cancellationToken = default) =>
        groupSettings.RemediateGroupSettingsAsync(groupEmail, cancellationToken);

    public Task<AllGroupsResult> GetAllDomainGroupsAsync(CancellationToken cancellationToken = default) =>
        groupSettings.GetAllDomainGroupsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<int> UpdateDriveFolderPathsAsync(CancellationToken cancellationToken = default) =>
        folderMaintenance.UpdateDriveFolderPathsAsync(cancellationToken);

    /// <inheritdoc />
    public Task SetInheritedPermissionsDisabledAsync(
        string googleFileId, bool restrict, CancellationToken cancellationToken = default) =>
        folderMaintenance.SetInheritedPermissionsDisabledAsync(googleFileId, restrict, cancellationToken);

    /// <inheritdoc />
    public Task<int> EnforceInheritedAccessRestrictionsAsync(CancellationToken cancellationToken = default) =>
        folderMaintenance.EnforceInheritedAccessRestrictionsAsync(cancellationToken);

    public Task<int> GetFailedSyncEventCountAsync(CancellationToken cancellationToken = default) =>
        outboxAdministration.GetFailedSyncEventCountAsync(cancellationToken);

    public Task<int> GetPendingSyncEventCountAsync(CancellationToken cancellationToken = default) =>
        outboxAdministration.GetPendingSyncEventCountAsync(cancellationToken);

    public Task<IReadOnlyList<GoogleSyncOutboxEventSnapshot>> GetRecentOutboxEventsAsync(int take, CancellationToken cancellationToken = default) =>
        outboxAdministration.GetRecentOutboxEventsAsync(take, cancellationToken);

    public Task<bool> RequeueOutboxEventAsync(Guid id, Guid actorUserId, CancellationToken cancellationToken = default) =>
        outboxAdministration.RequeueOutboxEventAsync(id, actorUserId, cancellationToken);

    public Task<int> RequeueAllFailedOutboxEventsAsync(Guid actorUserId, CancellationToken cancellationToken = default) =>
        outboxAdministration.RequeueAllFailedOutboxEventsAsync(actorUserId, cancellationToken);

    public Task<int> EnqueueUserSyncAsync(Guid userId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        outboxAdministration.EnqueueUserSyncAsync(userId, actorUserId, cancellationToken);

    private async Task<DrivePermissionLevel> ResolvePermissionLevelForUserAsync(
        string googleId, Guid userId, CancellationToken ct)
    {
        // A failing source throws out of here: the outbox retries the event, whereas a partial
        // roster would read as "no claim" and silently drop the new member's grant.
        var load = await DriveAccessClaims.LoadClaimsAsync(
            driveAccessSources, googleId, logger, ct, rethrowSourceFailures: true);
        var claim = load.Claims.SingleOrDefault(c => string.Equals(c.FolderId, googleId, StringComparison.OrdinalIgnoreCase));
        var level = claim?.Access.GetValueOrDefault(userId) ?? DrivePermissionLevel.None;
        if (level == DrivePermissionLevel.None)
            logger.LogWarning("Skipping Drive grant for {UserId} on {GoogleId}: no source claims the user's access", userId, googleId);
        return level;
    }


}
