using NodaTime;

namespace Humans.Expenses.Domain;

internal sealed class HoldedExpenseOutboxEvent
{
    public Guid Id { get; init; }
    public Guid ExpenseReportId { get; set; }
    public HoldedExpenseOutboxEventType EventType { get; set; }
    public Instant OccurredAt { get; init; }
    public Instant? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    /// <summary>
    /// Earliest instant the drain may pick this event up again. Null on a freshly-queued
    /// event (drain immediately); set to <c>now + 2^(RetryCount+1)</c> minutes after each
    /// transient failure, matching the Email outbox's backoff.
    /// </summary>
    public Instant? NextRetryAt { get; set; }
    public bool FailedPermanently { get; set; }
    /// <summary>
    /// When a drain run claimed this event (see <c>IExpenseRepository.TryClaimOutboxAsync</c>).
    /// Null when unclaimed; a claim older than the processor's lease is treated as abandoned.
    /// Released on retry, write-off and requeue.
    /// </summary>
    public Instant? ClaimedAt { get; set; }
    /// <summary>The drain run that holds the claim; null when unclaimed.</summary>
    public Guid? ClaimedBy { get; set; }
}
