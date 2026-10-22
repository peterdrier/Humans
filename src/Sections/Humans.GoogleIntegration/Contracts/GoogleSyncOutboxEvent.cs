using NodaTime;

namespace Humans.GoogleIntegration.Contracts;

public class GoogleSyncOutboxEvent
{
    public Guid Id { get; init; }
    public string EventType { get; set; } = string.Empty;
    public Guid TeamId { get; set; }
    public Guid UserId { get; set; }
    public Instant OccurredAt { get; init; }
    public Instant? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string DeduplicationKey { get; set; } = string.Empty;
    public string? LastError { get; set; }

    /// <summary>A permanent error (400/403/404): ProcessedAt is set and the event is never retried.</summary>
    public bool FailedPermanently { get; set; }
}
