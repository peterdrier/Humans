using Humans.Domain.Attributes;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Google Integration section's
/// <c>google_sync_outbox_events</c> table.
/// </summary>
/// <remarks>
/// Part 1 of issue #554 introduced this repository surface so Notifications
/// (<c>NotificationMeterProvider</c>) and Admin metrics
/// (<c>HumansMetricsService</c>) could reach failed, pending, and transient
/// retry counts without reading the table directly.
///
/// Part 2c of issue #576 extended the surface with the admin read for the
/// SyncOutbox view and the full processor cycle. Enqueue writes live here;
/// callers that need atomicity with another section's mutation wrap the two
/// repository calls in an ambient transaction from the application service.
///
/// Registered as Singleton via <c>IDbContextFactory&lt;HumansDbContext&gt;</c>.
/// </remarks>
[Section("GoogleIntegration")]
public interface IGoogleSyncOutboxRepository : IRepository
{
    // ==========================================================================
    // Write - enqueue
    // ==========================================================================

    Task AddAsync(GoogleSyncOutboxEvent outboxEvent, CancellationToken ct = default);

    Task AddRangeAsync(
        IReadOnlyCollection<GoogleSyncOutboxEvent> outboxEvents,
        CancellationToken ct = default);

    // ==========================================================================
    // Read - counts
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

    // ==========================================================================
    // Read - admin dashboard
    // ==========================================================================

    /// <summary>
    /// Returns up to <paramref name="take"/> outbox rows ordered by
    /// <c>OccurredAt</c> descending, for the admin <c>SyncOutbox</c> view.
    /// Read-only.
    /// </summary>
    Task<IReadOnlyList<GoogleSyncOutboxEvent>> GetRecentAsync(
        int take, CancellationToken ct = default);

    // ==========================================================================
    // Processor - used by ProcessGoogleSyncOutboxJob
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
    /// Marks an event as permanently failed (for example HTTP 400/403/404 from
    /// Google): sets <c>FailedPermanently = true</c>, stamps
    /// <c>ProcessedAt</c>, and stores <paramref name="lastError"/>
    /// (truncated to the 4000-char DB column width). No-op if the row is
    /// missing.
    /// </summary>
    Task MarkPermanentlyFailedAsync(
        Guid id, Instant processedAt, string lastError, CancellationToken ct = default);

    // ==========================================================================
    // Admin recovery
    // ==========================================================================

    /// <summary>
    /// Requeues a single failed outbox event for retry: clears
    /// <c>FailedPermanently</c>, <c>ProcessedAt</c>, <c>RetryCount</c>, and
    /// <c>LastError</c>. No-op if the row is missing or not in a failed state.
    /// Returns <c>true</c> if the event was found and reset.
    /// </summary>
    Task<bool> RequeueAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Requeues all permanently-failed outbox events: clears
    /// <c>FailedPermanently</c>, <c>ProcessedAt</c>, <c>RetryCount</c>, and
    /// <c>LastError</c> on every event where <c>FailedPermanently = true</c>.
    /// Returns the number of events reset.
    /// </summary>
    Task<int> RequeueAllFailedAsync(CancellationToken ct = default);

    /// <summary>
    /// Records a transient processing failure: increments <c>RetryCount</c>,
    /// stores <paramref name="lastError"/> (truncated to the 4000-char DB
    /// column width), and, if the new <c>RetryCount</c> has reached
    /// <paramref name="maxRetryCount"/>, also sets
    /// <c>FailedPermanently = true</c> and stamps <c>ProcessedAt</c> so the
    /// row drops out of the processing queue. Returns a flag indicating
    /// whether the retry budget was exhausted and the new <c>RetryCount</c>.
    /// Returns <c>(false, 0)</c> if the row is missing.
    /// </summary>
    Task<(bool ExhaustedRetries, int RetryCount)> IncrementRetryAsync(
        Guid id,
        Instant processedAt,
        string lastError,
        int maxRetryCount,
        CancellationToken ct = default);
}
