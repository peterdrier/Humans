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
    /// Null on a freshly queued event; <c>now + 2^(RetryCount+1)</c> minutes after each transient failure.
    /// </summary>
    public Instant? NextRetryAt { get; set; }
    public bool FailedPermanently { get; set; }
}
