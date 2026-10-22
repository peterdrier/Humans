using Humans.Base.Attributes;
using Humans.Base.Enums;

namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Group membership only: Drive access removal waits for <c>GoogleResourceReconciliationJob</c>. A scoped execute
/// that hits a Google API failure reschedules itself after the retry delay, up to the cap.
/// </summary>
public interface IGoogleGroupSync
{
    /// <summary>Deferred. Call after the DB commit.</summary>
    Task RequestSyncAsync(string groupKey, CancellationToken ct = default, GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync);

    /// <summary>
    /// Execute applies changes per the admin-configured <c>SyncMode</c>; Preview mutates nothing.
    /// <paramref name="syncSource"/> is recorded on the logs and carried into scoped retries.
    /// </summary>
    [ExternalWrite]
    Task<SyncPreviewResult> ReconcileAllAsync(
        SyncAction action,
        CancellationToken ct = default,
        GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync);

    [ExternalWrite]
    Task<ResourceSyncDiff> ReconcileOneAsync(
        string groupKey,
        SyncAction action,
        CancellationToken ct = default,
        int retryAttempt = 0,
        bool scheduleRetries = true,
        GoogleSyncSource syncSource = GoogleSyncSource.ScheduledSync);
}
