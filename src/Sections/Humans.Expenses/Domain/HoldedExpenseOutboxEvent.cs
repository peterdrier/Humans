using Humans.Domain.Enums;
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
}
