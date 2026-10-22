using Humans.Base.Attributes;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Contracts;

namespace Humans.GoogleIntegration.Services;

internal interface IGoogleSyncManagement : IGoogleSyncService, IGoogleSyncWrite, IApplicationService
{
    /// <summary>Group resources are routed through <see cref="IGoogleGroupSync"/>.</summary>
    [ExternalWrite]
    Task<ResourceSyncDiff> SyncSingleResourceAsync(
        Guid resourceId,
        SyncAction action,
        CancellationToken cancellationToken = default);

    /// <summary>Drive only; Group membership is <see cref="IGoogleGroupSync"/>'s.</summary>
    [ExternalWrite]
    Task<SyncPreviewResult> SyncResourcesByTypeAsync(
        GoogleResourceType resourceType,
        SyncAction action,
        CancellationToken cancellationToken = default,
        GoogleSyncSource syncSource = GoogleSyncSource.ManualSync);

    /// <summary>Detect-only. A skipped result when sync is disabled.</summary>
    Task<GroupSettingsDriftResult> CheckGroupSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns without action when sync is disabled.</summary>
    [ExternalWrite]
    Task<GroupSettingsRemediationResult> RemediateGroupSettingsAsync(string groupEmail, CancellationToken cancellationToken = default);

    Task<AllGroupsResult> GetAllDomainGroupsAsync(CancellationToken cancellationToken = default);

    /// <summary>Renames a resource whose folder moved or was renamed. Returns how many changed.</summary>
    Task<int> UpdateDriveFolderPathsAsync(CancellationToken cancellationToken = default);

    [ExternalWrite]
    Task SetInheritedPermissionsDisabledAsync(string googleFileId, bool restrict, CancellationToken cancellationToken = default);

    /// <summary>For folders with RestrictInheritedAccess on. Returns how many were corrected.</summary>
    [ExternalWrite]
    Task<int> EnforceInheritedAccessRestrictionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Audited against <paramref name="actorUserId"/> only when the event was found and reset.</summary>
    Task<bool> RequeueOutboxEventAsync(Guid id, Guid actorUserId, CancellationToken cancellationToken = default);

    /// <summary>Audited against <paramref name="actorUserId"/> even when nothing was reset.</summary>
    Task<int> RequeueAllFailedOutboxEventsAsync(Guid actorUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bypasses the <c>GoogleEmailStatus.Rejected</c> guard, so an admin can force a retry after fixing the email.
    /// Audited against <paramref name="actorUserId"/> even when nothing was enqueued.
    /// </summary>
    Task<int> EnqueueUserSyncAsync(Guid userId, Guid actorUserId, CancellationToken cancellationToken = default);

    Task<int> GetPendingSyncEventCountAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GoogleSyncOutboxEventSnapshot>> GetRecentOutboxEventsAsync(
        int take, CancellationToken cancellationToken = default);
}
