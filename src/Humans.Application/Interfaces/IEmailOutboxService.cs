namespace Humans.Application.Interfaces;

/// <summary>
/// Service for managing email outbox messages (retry, discard).
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
}
