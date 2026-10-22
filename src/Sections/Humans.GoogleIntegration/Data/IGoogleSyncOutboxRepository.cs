using Humans.GoogleIntegration.Contracts;
using NodaTime;
using Humans.Base.Interfaces.Repositories;

namespace Humans.GoogleIntegration.Data;

/// <summary>
/// Callers needing atomicity with another section's write wrap both repository calls in an ambient transaction.
/// </summary>
internal interface IGoogleSyncOutboxRepository : IRepository
{
    Task AddAsync(GoogleSyncOutboxEvent outboxEvent, CancellationToken ct = default);

    Task AddRangeAsync(
        IReadOnlyCollection<GoogleSyncOutboxEvent> outboxEvents,
        CancellationToken ct = default);

    /// <summary>Permanently failed, or unprocessed with a non-null <c>LastError</c>.</summary>
    Task<int> CountFailedAsync(CancellationToken ct = default);

    Task<int> CountPendingAsync(CancellationToken ct = default);

    Task<IReadOnlyList<GoogleSyncOutboxEvent>> GetRecentAsync(
        int take, CancellationToken ct = default);

    /// <summary>Pending events, oldest first. Detached: change them through the Mark and Increment methods.</summary>
    Task<IReadOnlyList<GoogleSyncOutboxEvent>> GetProcessingBatchAsync(
        int batchSize, int maxRetryCount, CancellationToken ct = default);

    /// <summary>Also clears <c>LastError</c>. No-op if the row is missing.</summary>
    Task MarkProcessedAsync(Guid id, Instant processedAt, CancellationToken ct = default);

    /// <summary>
    /// For errors a retry cannot fix, such as Google 400/403/404. <paramref name="lastError"/> is truncated to the
    /// column width.
    /// </summary>
    Task MarkPermanentlyFailedAsync(
        Guid id, Instant processedAt, string lastError, CancellationToken ct = default);

    /// <summary>False when the row is missing or not failed.</summary>
    Task<bool> RequeueAsync(Guid id, CancellationToken ct = default);

    Task<int> RequeueAllFailedAsync(CancellationToken ct = default);

    /// <summary>
    /// Once <c>RetryCount</c> reaches <paramref name="maxRetryCount"/>, also fails the event permanently so it
    /// leaves the queue. Returns <c>(false, 0)</c> if the row is missing.
    /// </summary>
    Task<(bool ExhaustedRetries, int RetryCount)> IncrementRetryAsync(
        Guid id,
        Instant processedAt,
        string lastError,
        int maxRetryCount,
        CancellationToken ct = default);
}
