using Humans.Base.Attributes;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.Google.Contracts;
using NodaTime;

namespace Humans.Google.Services;

internal interface IGoogleSyncManagement : IGoogleSyncService, IGoogleSyncWrite, IApplicationService
{
    /// <summary>Group resources are routed through <see cref="IGoogleGroupSync"/>.</summary>
    [ExternalWrite]
    Task<ResourceSyncDiff> SyncSingleResourceAsync(
        Guid resourceId,
        SyncAction action,
        CancellationToken cancellationToken = default);

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

    /// <summary>Unprocessed events with a <c>LastError</c>.</summary>
    Task<int> GetFailedSyncEventCountAsync(CancellationToken cancellationToken = default);

    Task<int> GetPendingSyncEventCountAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GoogleSyncOutboxEventSnapshot>> GetRecentOutboxEventsAsync(
        int take, CancellationToken cancellationToken = default);
}

internal sealed record GoogleSyncOutboxEventSnapshot(
    Guid Id,
    string EventType,
    Guid TeamId,
    Guid UserId,
    Instant OccurredAt,
    Instant? ProcessedAt,
    int RetryCount,
    string? LastError,
    bool FailedPermanently);

internal sealed record GroupSettingsRemediationResult(bool Succeeded, string? ErrorMessage)
{
    public static GroupSettingsRemediationResult Success() => new(true, null);

    public static GroupSettingsRemediationResult Failure(string message) => new(false, message);
}
