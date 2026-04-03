using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Outbox message representing a deferred Google sync membership operation.
/// </summary>
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

    /// <summary>
    /// Whether this event failed with a permanent error (HTTP 400/404).
    /// When true, ProcessedAt is set and the event is not retried.
    /// </summary>
    public bool FailedPermanently { get; set; }
}
