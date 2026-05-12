using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class HoldedExpenseOutboxEvent
{
    public Guid Id { get; init; }
    public Guid ExpenseReportId { get; set; }
    public HoldedExpenseOutboxEventType EventType { get; set; }
    public Instant OccurredAt { get; init; }
    public Instant? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public bool FailedPermanently { get; set; }
}
