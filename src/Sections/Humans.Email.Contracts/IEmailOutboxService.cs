using Humans.Base.Interfaces;
using Humans.Base.Enums;
using NodaTime;

namespace Humans.Email.Contracts;

/// <summary>Per-user outbox history, for other sections.</summary>
public interface IEmailOutboxService : IApplicationService
{
    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<EmailOutboxMessageDto>> GetMessagesForUserAsync(
        Guid userId, CancellationToken cancellationToken = default);

    Task<int> GetMessageCountForUserAsync(
        Guid userId, CancellationToken cancellationToken = default);
}

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
