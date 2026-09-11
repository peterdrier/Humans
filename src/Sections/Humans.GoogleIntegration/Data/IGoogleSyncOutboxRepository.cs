using Humans.GoogleIntegration.Contracts;
using NodaTime;
using Humans.Base.Interfaces.Repositories;

namespace Humans.GoogleIntegration.Data;

/// <summary>
/// Repository for the Google Integration section's
/// <c>google_sync_outbox</c> table.
/// </summary>
/// <remarks>
/// Enqueue writes live here; callers that need atomicity with another section's mutation
/// wrap the two repository calls in an ambient transaction from the application service.
///
/// Registered as Singleton via <c>IDbContextFactory&lt;GoogleIntegrationDbContext&gt;</c>.
/// </remarks>
internal interface IGoogleSyncOutboxRepository : IRepository
{
    Task AddAsync(GoogleSyncOutboxEvent outboxEvent, CancellationToken ct = default);

    Task AddRangeAsync(
        IReadOnlyCollection<GoogleSyncOutboxEvent> outboxEvents,
        CancellationToken ct = default);

    /// <summary>
    /// Counts failed outbox events: permanently failed, or unprocessed with a non-null
    /// <c>LastError</c>. Read-only.
    /// </summary>
    Task<int> CountFailedAsync(CancellationToken ct = default);

    /// <summary>
    /// Counts all currently unprocessed outbox events
    /// (<c>ProcessedAt == null</c>). Used by <c>IHumansMetrics</c> to expose
    /// a pending-queue-size gauge. Read-only.
    /// </summary>
    Task<int> CountPendingAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns up to <paramref name="take"/> outbox rows ordered by
    /// <c>OccurredAt</c> descending, for the admin <c>SyncOutbox</c> view.
    /// Read-only.
    /// </summary>
    Task<IReadOnlyList<GoogleSyncOutboxEvent>> GetRecentAsync(
        int take, CancellationToken ct = default);

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
