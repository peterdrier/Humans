using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Services.Email;

/// <summary>
/// Application-layer implementation of <see cref="IEmailOutboxService"/>:
/// admin-dashboard reads (stats, recent messages, per-user history) and admin
/// writes (retry, discard, pause/resume) over <see cref="IEmailOutboxRepository"/>.
/// Authoritative gateway for the <c>IsEmailSendingPaused</c> flag; the background
/// processor job reads it through the repository directly (Singleton→Scoped).
/// </summary>
public sealed class EmailOutboxService : IEmailOutboxService
{
    private static readonly Duration Last24Hours = Duration.FromHours(24);

    private readonly IEmailOutboxRepository _repo;
    private readonly IClock _clock;

    public EmailOutboxService(IEmailOutboxRepository repo, IClock clock)
    {
        _repo = repo;
        _clock = clock;
    }

    public Task<string?> RetryMessageAsync(Guid id, CancellationToken cancellationToken = default) =>
        _repo.RetryAsync(id, cancellationToken);

    public Task<string?> DiscardMessageAsync(Guid id, CancellationToken cancellationToken = default) =>
        _repo.DiscardAsync(id, cancellationToken);

    public async Task<EmailOutboxStats> GetOutboxStatsAsync(
        int recentMessageCount = 50, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var cutoff24H = now - Last24Hours;

        var totalCount = await _repo.GetTotalCountAsync(cancellationToken);
        var queuedCount = await _repo.GetCountByStatusAsync(EmailOutboxStatus.Queued, cancellationToken);
        var sentLast24H = await _repo.GetSentCountSinceAsync(cutoff24H, cancellationToken);
        var failedCount = await _repo.GetCountByStatusAsync(EmailOutboxStatus.Failed, cancellationToken);
        var isPaused = await _repo.GetSendingPausedAsync(cancellationToken);
        var messages = await _repo.GetRecentAsync(recentMessageCount, cancellationToken);

        return new EmailOutboxStats(
            totalCount,
            queuedCount,
            sentLast24H,
            failedCount,
            isPaused,
            messages.Select(ToDto).ToList());
    }

    public async Task<IReadOnlyList<EmailOutboxMessageDto>> GetMessagesForUserAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var messages = await _repo.GetForUserAsync(userId, cancellationToken);
        return messages.Select(ToDto).ToList();
    }

    public Task<int> GetMessageCountForUserAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        _repo.GetCountForUserAsync(userId, cancellationToken);

    public Task<bool> IsEmailPausedAsync(CancellationToken cancellationToken = default) =>
        _repo.GetSendingPausedAsync(cancellationToken);

    public Task SetEmailPausedAsync(bool paused, CancellationToken cancellationToken = default) =>
        _repo.SetSendingPausedAsync(paused, cancellationToken);

    private static EmailOutboxMessageDto ToDto(EmailOutboxMessage message) => new(
        message.Id,
        message.RecipientEmail,
        message.RecipientName,
        message.Subject,
        message.HtmlBody,
        message.TemplateName,
        message.UserId,
        message.Status,
        message.CreatedAt,
        message.SentAt,
        message.RetryCount,
        message.LastError);
}
