namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Google Integration section's
/// <c>google_sync_outbox_events</c> table.
/// </summary>
/// <remarks>
/// Part 1 of issue #554 (Google Workspace §15 migration) introduces this
/// repository surface so Notifications (<c>NotificationMeterProvider</c>),
/// Admin metrics (<c>HumansMetricsService</c>), and the Admin daily digest
/// (<c>SendAdminDailyDigestJob</c>) can reach the failed / pending /
/// transient-retry counts without reading the table directly
/// (design-rules §2c).
///
/// Part 2 will extend this repository with the full write/enqueue/dequeue
/// surface currently owned by <c>GoogleWorkspaceSyncService</c> and
/// <c>ProcessGoogleSyncOutboxJob</c>, and flip those consumers over.
///
/// Registered as Singleton via <c>IDbContextFactory&lt;HumansDbContext&gt;</c>
/// per design-rules §15b.
/// </remarks>
public interface IGoogleSyncOutboxRepository
{
    /// <summary>
    /// Counts unprocessed outbox events that carry a non-null <c>LastError</c>.
    /// Matches the pre-migration inline query
    /// <c>e.ProcessedAt == null &amp;&amp; e.LastError != null</c>. Read-only.
    /// </summary>
    Task<int> CountFailedAsync(CancellationToken ct = default);

    /// <summary>
    /// Counts all currently unprocessed outbox events
    /// (<c>ProcessedAt == null</c>). Used by <c>IHumansMetrics</c> to expose
    /// a pending-queue-size gauge. Read-only.
    /// </summary>
    Task<int> CountPendingAsync(CancellationToken ct = default);

    /// <summary>
    /// Counts stale sync events — unprocessed, carrying an error, but not
    /// permanently failed. Matches the pre-migration inline query
    /// <c>e.ProcessedAt == null &amp;&amp; e.LastError != null &amp;&amp; !e.FailedPermanently</c>.
    /// Surfaced in the Admin daily digest. Read-only.
    /// </summary>
    Task<int> CountStaleAsync(CancellationToken ct = default);

    /// <summary>
    /// Counts unprocessed events currently in transient-retry state
    /// (<c>ProcessedAt == null &amp;&amp; !FailedPermanently &amp;&amp; RetryCount &gt; 0</c>).
    /// Surfaced in the Admin daily digest. Read-only.
    /// </summary>
    Task<int> CountTransientRetriesAsync(CancellationToken ct = default);
}
