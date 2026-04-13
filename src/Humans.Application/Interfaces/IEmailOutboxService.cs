using Humans.Domain.Entities;

namespace Humans.Application.Interfaces;

/// <summary>
/// Service for managing email outbox messages (retry, discard, stats, pause/resume).
/// </summary>
public interface IEmailOutboxService
{
    /// <summary>
    /// Requeues a failed or stuck email outbox message for retry.
    /// Returns the recipient email if found, or null if the message does not exist.
    /// </summary>
    Task<string?> RetryMessageAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards (deletes) an email outbox message.
    /// Returns the recipient email if found, or null if the message does not exist.
    /// </summary>
    Task<string?> DiscardMessageAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets aggregate statistics and recent messages for the email outbox dashboard.
    /// </summary>
    Task<EmailOutboxStats> GetOutboxStatsAsync(int recentMessageCount = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets whether email sending is currently paused.
    /// </summary>
    Task<bool> IsEmailPausedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the email sending paused state.
    /// </summary>
    Task SetEmailPausedAsync(bool paused, CancellationToken cancellationToken = default);
}

/// <summary>
/// Aggregate statistics for the email outbox dashboard.
/// </summary>
public record EmailOutboxStats(
    int TotalCount,
    int QueuedCount,
    int SentLast24HoursCount,
    int FailedCount,
    bool IsPaused,
    IReadOnlyList<EmailOutboxMessage> RecentMessages);
