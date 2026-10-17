using Humans.Base.Attributes;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Contracts;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// Full Google sync surface. Other sections depend on <see cref="IGoogleSyncService"/> for reads and
/// <see cref="IGoogleSyncWrite"/> for the few writes they make; this adds the sync and
/// remediation workflows consumed inside the section.
/// </summary>
internal interface IGoogleSyncManagement : IGoogleSyncService, IGoogleSyncWrite, IApplicationService
{
    /// <summary>
    /// Drive sync entry point. Computes diff for all active Drive resources of the given
    /// type, then optionally executes adds/removes based on the action. Google Group
    /// membership is handled by <see cref="IGoogleGroupSync"/>.
    /// </summary>
    /// <param name="resourceType">The kind of Drive resources to reconcile.</param>
    /// <param name="action">Whether to preview the changes or execute them.</param>
    /// <param name="cancellationToken">Token used to cancel the sync.</param>
    /// <param name="syncSource">The trigger recorded on any Drive sync-log rows.</param>
    [ExternalWrite]
    Task<SyncPreviewResult> SyncResourcesByTypeAsync(
        GoogleResourceType resourceType,
        SyncAction action,
        CancellationToken cancellationToken = default,
        GoogleSyncSource syncSource = GoogleSyncSource.ManualSync);

    /// <summary>
    /// Checks all active Google Groups for settings drift against the expected configuration.
    /// Detect-only: does not modify any settings.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Drift results for all groups, or a skipped result if sync is disabled.</returns>
    Task<GroupSettingsDriftResult> CheckGroupSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies expected settings to a Google Group, fixing any drift.
    /// Respects SyncSettings mode — returns without action if sync is disabled.
    /// </summary>
    [ExternalWrite]
    Task<GroupSettingsRemediationResult> RemediateGroupSettingsAsync(string groupEmail, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all Google Groups on the domain and cross-references with the local database.
    /// Returns drift status for each group relative to the expected settings.
    /// </summary>
    Task<AllGroupsResult> GetAllDomainGroupsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the current Drive folder path for each active Drive resource
    /// and updates GoogleResource.Name when the folder has been moved or renamed.
    /// Called during nightly reconciliation.
    /// </summary>
    /// <returns>Number of resources whose name was updated.</returns>
    Task<int> UpdateDriveFolderPathsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets inheritedPermissionsDisabled on a Google Drive folder.
    /// When restrict is true, disables inherited permissions; when false, re-enables them.
    /// </summary>
    /// <param name="googleFileId">The Google Drive file/folder ID.</param>
    /// <param name="restrict">True to disable inheritance, false to enable.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [ExternalWrite]
    Task SetInheritedPermissionsDisabledAsync(string googleFileId, bool restrict, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks and corrects inherited access drift for all Drive folders that have
    /// RestrictInheritedAccess enabled. Returns the number of folders corrected.
    /// </summary>
    [ExternalWrite]
    Task<int> EnforceInheritedAccessRestrictionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Requeues a single permanently-failed outbox event for retry.
    /// Returns <c>true</c> if the event was found and reset.
    /// </summary>
    Task<bool> RequeueOutboxEventAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requeues all permanently-failed outbox events.
    /// Returns the number of events reset.
    /// </summary>
    Task<int> RequeueAllFailedOutboxEventsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues <see cref="GoogleSyncOutboxEventTypes.AddUserToTeamResources"/> events
    /// for all teams the given user currently belongs to, bypassing the
    /// <c>GoogleEmailStatus.Rejected</c> (Humans.Users.Contracts) guard so an admin can force a
    /// re-attempt after fixing the user's email. Returns the number of events enqueued.
    /// </summary>
    Task<int> EnqueueUserSyncAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the count of outbox events that have not yet been processed
    /// (<c>ProcessedAt == null</c>), for the Admin pending-queue-size gauge.
    /// </summary>
    Task<int> GetPendingSyncEventCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the most recent Google sync outbox events for the admin
    /// dashboard, ordered newest-first and capped by <paramref name="take"/>.
    /// Keeps <c>google_sync_outbox</c> reads inside the owning service
    /// (design-rules §2a/§2c) so callers do not reach past
    /// <see cref="IGoogleSyncService"/> into the repository directly.
    /// </summary>
    Task<IReadOnlyList<GoogleSyncOutboxEventSnapshot>> GetRecentOutboxEventsAsync(
        int take, CancellationToken cancellationToken = default);
}
