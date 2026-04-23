using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Google Integration section's
/// <c>google_sync_outbox_events</c> table.
/// </summary>
/// <remarks>
/// Part 1 of issue #554 (Google Workspace §15 migration) introduced this
/// repository surface so Notifications (<c>NotificationMeterProvider</c>),
/// Admin metrics (<c>HumansMetricsService</c>), and the Admin daily digest
/// (<c>SendAdminDailyDigestJob</c>) could reach the failed / pending /
/// transient-retry counts without reading the table directly
/// (design-rules §2c).
///
/// Part 2c (issue #576) extended the surface with the admin read for the
/// SyncOutbox view and the full processor cycle (claim / mark-processed /
/// mark-permanently-failed / increment-retry) so
/// <see cref="Humans.Infrastructure.Jobs.ProcessGoogleSyncOutboxJob"/> and
/// <c>GoogleController.SyncOutbox</c> stop touching <c>HumansDbContext</c>
/// directly. Enqueue writes still flow through
/// <c>TeamRepository.AddMemberWithOutboxAsync</c> /
/// <c>ApproveRequestWithMemberAsync</c> /
/// <c>MarkMemberLeftWithOutboxAsync</c> because the outbox row must be
/// persisted atomically with the team-member state change — those stay
/// inside the Teams transaction boundary (§6d).
///
/// Registered as Singleton via <c>IDbContextFactory&lt;HumansDbContext&gt;</c>
/// per design-rules §15b.
/// </remarks>
public interface IGoogleSyncOutboxRepository
{
    // ==========================================================================
    // Read — counts (Part 1)
    // ==========================================================================

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

    // ==========================================================================
    // Read — admin dashboard (Part 2c)
    // ==========================================================================

    /// <summary>
    /// Returns up to <paramref name="take"/> outbox rows ordered by
    /// <c>OccurredAt</c> descending, for the admin <c>SyncOutbox</c> view.
    /// Read-only.
    /// </summary>
    Task<IReadOnlyList<GoogleSyncOutboxEvent>> GetRecentAsync(
        int take, CancellationToken ct = default);

    // ==========================================================================
    // Processor — used by ProcessGoogleSyncOutboxJob (Part 2c)
    // ==========================================================================

    /// <summary>
    /// Loads up to <paramref name="batchSize"/> pending events
    /// (<c>ProcessedAt == null &amp;&amp; !FailedPermanently &amp;&amp; RetryCount &lt; maxRetryCount</c>)
    /// ordered by <c>OccurredAt</c> ascending. Returned entities are detached;
    /// to update, use <see cref="MarkProcessedAsync"/>,
    /// <see cref="MarkPermanentlyFailedAsync"/>, or
    /// <see cref="IncrementRetryAsync"/>.
    /// </summary>
    Task<IReadOnlyList<GoogleSyncOutboxEvent>> GetProcessingBatchAsync(
        int batchSize, int maxRetryCount, CancellationToken ct = default);

    /// <summary>
    /// Marks an event processed successfully: sets <c>ProcessedAt</c> and
    /// clears <c>LastError</c>. No-op if the row is missing.
    /// </summary>
    Task MarkProcessedAsync(Guid id, Instant processedAt, CancellationToken ct = default);

    /// <summary>
    /// Marks an event as permanently failed (e.g. HTTP 400/403/404 from
    /// Google): sets <c>FailedPermanently = true</c>, stamps
    /// <c>ProcessedAt</c>, and stores <paramref name="lastError"/>
    /// (truncated to the 4000-char DB column width). No-op if the row is
    /// missing.
    /// </summary>
    Task MarkPermanentlyFailedAsync(
        Guid id, Instant processedAt, string lastError, CancellationToken ct = default);

    /// <summary>
    /// Records a transient processing failure: increments <c>RetryCount</c>,
    /// stores <paramref name="lastError"/> (truncated to the 4000-char DB
    /// column width), and — if the new <c>RetryCount</c> has reached
    /// <paramref name="maxRetryCount"/> — also sets
    /// <c>FailedPermanently = true</c> and stamps <c>ProcessedAt</c> so the
    /// row drops out of the processing queue. Returns a flag indicating
    /// whether the retry budget was exhausted (so the caller can dispatch the
    /// final-failure admin notification) and the new <c>RetryCount</c>.
    /// Returns <c>(false, 0)</c> if the row is missing.
    /// </summary>
    Task<(bool ExhaustedRetries, int RetryCount)> IncrementRetryAsync(
        Guid id,
        Instant processedAt,
        string lastError,
        int maxRetryCount,
        CancellationToken ct = default);
}
