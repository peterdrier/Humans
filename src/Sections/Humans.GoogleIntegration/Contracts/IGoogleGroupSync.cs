using Humans.Base.Attributes;
using Humans.Base.Enums;

namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Orchestrates Google Group membership sync. Unions every registered
/// <see cref="IGoogleGroupMembershipSource"/>'s expected member set per group,
/// detects collisions (two sources claiming the same group), hydrates user IDs
/// and applies user-state filtering uniformly, then diffs against Google and
/// applies changes through the existing
/// <see cref="Humans.GoogleIntegration.Services.Workspace.IGoogleGroupMembershipClient"/> connector.
/// </summary>
/// <remarks>
/// <para>
/// Scoped sync requests are queued through Hangfire. When a scoped execute
/// pass hits a Google API failure, the orchestrator schedules another scoped
/// execute pass for the same group key after the configured retry delay, up
/// to the scoped retry cap.
/// </para>
/// <para>
/// Drive folder permissions are not handled here. Drive access removal
/// is deferred to the scheduled <c>GoogleResourceReconciliationJob</c>
/// reconciliation pass. Group membership is the concern of this
/// orchestrator alone.
/// </para>
/// </remarks>
public interface IGoogleGroupSync
{
    /// <summary>
    /// Enqueues a deferred reconcile for one group key. Called by section
    /// services after a DB commit (e.g. team membership change).
    /// </summary>
    Task RequestSyncAsync(string groupKey, CancellationToken ct = default, GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync);

    /// <summary>
    /// Reconciles every group claimed by any registered source. Used by the
    /// daily <c>GoogleResourceReconciliationJob</c> and the <c>/Google/Sync</c>
    /// Groups tab's preview-all and execute-all flows.
    /// </summary>
    /// <param name="action">
    /// <see cref="SyncAction.Preview"/> computes the diff without mutating
    /// Google; <see cref="SyncAction.Execute"/> applies changes per the
    /// admin-configured <c>SyncSettings</c> mode (None / AddOnly / AddAndRemove).
    /// </param>
    /// <param name="ct">Token used to cancel reconciliation.</param>
    /// <param name="syncSource">Trigger recorded on sync logs and carried into scoped retries.</param>
    [ExternalWrite]
    Task<SyncPreviewResult> ReconcileAllAsync(
        SyncAction action,
        CancellationToken ct = default,
        GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync);

    /// <summary>
    /// Reconciles one group. Called by Hangfire-scoped sync requests and by
    /// the <c>/Google/Sync</c> Groups tab's per-row Execute.
    /// </summary>
    [ExternalWrite]
    Task<ResourceSyncDiff> ReconcileOneAsync(
        string groupKey,
        SyncAction action,
        CancellationToken ct = default,
        int retryAttempt = 0,
        bool scheduleRetries = true,
        GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync);
}
