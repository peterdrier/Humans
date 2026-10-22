using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Services.Workspace;
using Humans.Base.Enums;
using Humans.AuditLog.Contracts;

namespace Humans.GoogleIntegration.Services;

internal sealed class StubGoogleSyncService(
    ILogger<StubGoogleSyncService> logger,
    IGoogleSyncOutboxRepository outboxRepository,
    IGoogleDrivePermissionsClient drivePermissions,
    IGoogleDriveAccessSyncScheduler driveAccessSyncScheduler,
    IAuditLogService auditLogService) : IGoogleSyncManagement
{
    public async Task<string> CreateSubfolderAsync(
        string parentFolderId,
        string name,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[STUB] Would create Drive subfolder '{Name}' under {ParentFolderId}", name, parentFolderId);
        var result = await drivePermissions.CreateFolderAsync(parentFolderId, name, cancellationToken);
        if (result.FolderId is null)
        {
            throw new InvalidOperationException(
                $"[STUB] Failed to create Drive subfolder '{name}' under {parentFolderId}: {result.Error?.RawMessage}");
        }

        return result.FolderId;
    }

    // The stub creates subfolders under any parent, so every folder counts as reachable.
    public Task<bool> IsFolderReachableAsync(string folderId, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task RequestSyncAsync(string folderId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderId))
        {
            throw new ArgumentException("Folder id is required.", nameof(folderId));
        }

        logger.LogInformation("[STUB] Would request Drive access reconcile for folder {FolderId}", folderId);
        driveAccessSyncScheduler.Enqueue(folderId.Trim());
        return Task.CompletedTask;
    }

    public Task<GoogleResourceGrantResult> AddUserToTeamResourcesAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default,
        GoogleSyncSource syncSource = GoogleSyncSource.ManualSync)
    {
        logger.LogInformation("[STUB] Would add user {UserId} to team {TeamId} Google resources", userId, teamId);
        return Task.FromResult(new GoogleResourceGrantResult(GoogleResourceGrantOutcome.Deferred, userId));
    }

    public Task<GroupLinkResult> EnsureTeamGroupAsync(Guid teamId, bool confirmReactivation = false, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[STUB] Would ensure Google Group exists for team {TeamId}", teamId);
        return Task.FromResult(GroupLinkResult.Ok());
    }

    public Task<GroupSettingsDriftResult> CheckGroupSettingsAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[STUB] Would check Google Group settings for drift");
        return Task.FromResult(new GroupSettingsDriftResult());
    }

    public Task<GroupSettingsRemediationResult> RemediateGroupSettingsAsync(string groupEmail, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[STUB] Would remediate settings for Google Group {GroupEmail}", groupEmail);
        return Task.FromResult(GroupSettingsRemediationResult.Success());
    }

    public Task<AllGroupsResult> GetAllDomainGroupsAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[STUB] Would enumerate all domain groups");
        return Task.FromResult(new AllGroupsResult());
    }

    public Task<int> UpdateDriveFolderPathsAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[STUB] Would update Drive folder paths");
        return Task.FromResult(0);
    }

    public Task<SyncPreviewResult> SyncResourcesByTypeAsync(
        GoogleResourceType resourceType,
        SyncAction action,
        CancellationToken cancellationToken = default,
        GoogleSyncSource syncSource = GoogleSyncSource.ManualSync)
    {
        logger.LogInformation("[STUB] Would sync resources of type {ResourceType} with action {Action}", resourceType, action);
        return Task.FromResult(new SyncPreviewResult());
    }

    public Task<ResourceSyncDiff> SyncSingleResourceAsync(
        Guid resourceId,
        SyncAction action,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[STUB] Would sync single resource {ResourceId} with action {Action}", resourceId, action);
        return Task.FromResult(new ResourceSyncDiff { ResourceId = resourceId });
    }

    public Task SetInheritedPermissionsDisabledAsync(string googleFileId, bool restrict, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[STUB] Would set inheritedPermissionsDisabled={Restrict} on Drive file {GoogleFileId}", restrict, googleFileId);
        return Task.CompletedTask;
    }

    public Task<int> EnforceInheritedAccessRestrictionsAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[STUB] Would enforce inherited access restrictions on Drive folders");
        return Task.FromResult(0);
    }

    public Task<int> GetFailedSyncEventCountAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(0);
    }

    public Task<int> GetPendingSyncEventCountAsync(CancellationToken cancellationToken = default)
    {
        // Not stubbed: the outbox is real here, only the Google writes are stubbed. Returning 0 would make
        // humans.google_sync_outbox_pending wrong wherever credentials are missing.
        return outboxRepository.CountPendingAsync(cancellationToken);
    }

    public Task<IReadOnlyList<GoogleSyncOutboxEventSnapshot>> GetRecentOutboxEventsAsync(
        int take, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<GoogleSyncOutboxEventSnapshot>>([]);
    }

    public Task<bool> RequeueOutboxEventAsync(Guid id, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        // Nothing is requeued, so nothing is audited (as in the real service).
        logger.LogInformation("[STUB] Would requeue Google sync outbox event {Id}", id);
        return Task.FromResult(false);
    }

    public async Task<int> RequeueAllFailedOutboxEventsAsync(Guid actorUserId, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[STUB] Would requeue all permanently-failed Google sync outbox events");
        await GoogleWorkspaceSyncService.AuditRequeueAllAsync(auditLogService, 0, actorUserId);
        return 0;
    }

    public async Task<int> EnqueueUserSyncAsync(Guid userId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[STUB] Would enqueue Google sync re-run for user {UserId}", userId);
        await GoogleWorkspaceSyncService.AuditUserSyncRerunAsync(auditLogService, userId, 0, actorUserId);
        return 0;
    }
}
