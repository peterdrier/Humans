using Humans.Base.Interfaces;
using Humans.Base.Enums;
using NodaTime;

namespace Humans.Email.Services;

/// <summary>Per-user outbox history.</summary>
internal interface IEmailOutboxService : IApplicationService
{
    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<EmailOutboxMessageDto>> GetMessagesForUserAsync(
        Guid userId, CancellationToken cancellationToken = default);

    Task<int> GetMessageCountForUserAsync(
        Guid userId, CancellationToken cancellationToken = default);
}

internal sealed record EmailOutboxMessageDto(
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
