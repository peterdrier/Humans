namespace Humans.Application.Interfaces.Email;

/// <summary>
/// Cross-section read surface for the Email outbox. External sections inject
/// this interface; only per-user outbox history projections
/// (<see cref="EmailOutboxMessageDto"/> / counts), no admin writes or
/// dashboard/pause state. See
/// <c>memory/architecture/section-read-write-split.md</c>.
/// </summary>
public interface IEmailOutboxServiceRead
{
    /// <summary>
    /// Gets outbox messages for a specific user, ordered by CreatedAt descending.
    /// </summary>
    Task<IReadOnlyList<EmailOutboxMessageDto>> GetMessagesForUserAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the count of outbox messages for a specific user.
    /// </summary>
    Task<int> GetMessageCountForUserAsync(
        Guid userId, CancellationToken cancellationToken = default);
}
