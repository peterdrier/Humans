using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Email.Contracts;

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

/// <summary>
/// One outbox row as the profile and user-admin outbox pages read it. On the leaf rather
/// than internal because <see cref="IEmailOutboxServiceRead"/> returns it and both call
/// sites are Shell's. <see cref="EmailOutboxStatus"/> stays Base vocabulary: Campaigns
/// and Surveys persist it on their own entities.
/// </summary>
public sealed record EmailOutboxMessageDto(
    Guid Id,
    string RecipientEmail,
    string? RecipientName,
    string Subject,
    string HtmlBody,
    string TemplateName,
    Guid? UserId,
    EmailOutboxStatus Status,
    Instant CreatedAt,
    Instant? SentAt,
    int RetryCount,
    string? LastError);
