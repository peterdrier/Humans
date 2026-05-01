using Humans.Application.DTOs;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.GoogleIntegration;

/// <summary>
/// Orchestrates Google Group membership sync. Unions every registered
/// <see cref="IGoogleGroupMembershipSource"/>'s expected member set per group,
/// detects collisions (two sources claiming the same group), hydrates user IDs
/// and applies user-state filtering uniformly, then diffs against Google and
/// applies changes through the existing
/// <see cref="IGoogleGroupMembershipClient"/> connector.
/// </summary>
/// <remarks>
/// <para>
/// Retry on transient Google failure flows through the existing
/// <c>google_sync_outbox_events</c> table — the new
/// <c>ReconcileGroupMembership</c> event type stores the group email in
/// <c>DeduplicationKey</c>, which gives natural coalescing of multiple
/// changes-in-quick-succession into a single reconcile pass.
/// </para>
/// <para>
/// Drive folder permissions are <em>not</em> handled here — they remain
/// per-user-per-team via <c>IGoogleSyncService.AddUserToTeamResourcesAsync</c>
/// and <c>RemoveUserFromTeamResourcesAsync</c>. Group membership is the
/// concern of this orchestrator alone.
/// </para>
/// </remarks>
public interface IGoogleGroupSync
{
    /// <summary>
    /// Enqueues a deferred reconcile for one group key. Called by section
    /// services after a DB commit (e.g. team membership change). Coalesces
    /// via <c>DeduplicationKey</c> — multiple calls for the same key while a
    /// previous event is still pending collapse into one reconcile pass.
    /// </summary>
    Task RequestSyncAsync(string groupKey, CancellationToken ct = default);

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
    Task<SyncPreviewResult> ReconcileAllAsync(
        SyncAction action,
        CancellationToken ct = default);

    /// <summary>
    /// Reconciles one group. Throws on Google API failure so the outbox
    /// processor can record retry state via the standard
    /// <c>RetryCount</c> + <c>LastError</c> pattern. Called by
    /// <c>ProcessGoogleSyncOutboxJob</c> when draining a
    /// <c>ReconcileGroupMembership</c> event, and by the <c>/Google/Sync</c>
    /// Groups tab's per-row Execute.
    /// </summary>
    Task<ResourceSyncDiff> ReconcileOneAsync(
        string groupKey,
        SyncAction action,
        CancellationToken ct = default);
}
