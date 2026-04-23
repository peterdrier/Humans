using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Services.Email;

/// <summary>
/// Application-layer implementation of <see cref="IEmailOutboxService"/>.
/// Fronts <see cref="IEmailOutboxRepository"/> for admin-dashboard reads
/// (stats, recent messages, per-user history) and admin writes (retry,
/// discard, pause/resume). The service owns no business rules beyond
/// composing the <see cref="EmailOutboxStats"/> shape — it is strictly a
/// boundary over the repository.
/// </summary>
/// <remarks>
/// The <c>IsEmailSendingPaused</c> row in <c>system_settings</c> is the
/// Email section's operational state, so this service is the authoritative
/// gateway for that flag. The background processor job reads it through
/// <see cref="IEmailOutboxRepository"/> directly (job is Infrastructure) to
/// avoid Singleton→Scoped capture.
/// </remarks>
public sealed class EmailOutboxService : IEmailOutboxService
{
    // Same shape as the retention window used by the admin dashboard;
    // moved here as a named constant instead of re-deriving inline.
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

        // Five independent reads. At ~500-user scale — and with
        // <c>email_outbox_messages</c> purged weekly by CleanupEmailOutboxJob —
        // the cost is trivial and the clarity wins over a hand-rolled grouped
        // aggregation.
        var totalCount = await _repo.GetTotalCountAsync(cancellationToken);
        var queuedCount = await _repo.GetCountByStatusAsync(EmailOutboxStatus.Queued, cancellationToken);
        var sentLast24H = await _repo.GetSentCountSinceAsync(cutoff24H, cancellationToken);
        var failedCount = await _repo.GetCountByStatusAsync(EmailOutboxStatus.Failed, cancellationToken);
        var isPaused = await _repo.GetSendingPausedAsync(cancellationToken);
        var messages = await _repo.GetRecentAsync(recentMessageCount, cancellationToken);

        return new EmailOutboxStats(totalCount, queuedCount, sentLast24H, failedCount, isPaused, messages);
    }

    public Task<IReadOnlyList<EmailOutboxMessage>> GetMessagesForUserAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        _repo.GetForUserAsync(userId, cancellationToken);

    public Task<int> GetMessageCountForUserAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        _repo.GetCountForUserAsync(userId, cancellationToken);

    public Task<bool> IsEmailPausedAsync(CancellationToken cancellationToken = default) =>
        _repo.GetSendingPausedAsync(cancellationToken);

    public Task SetEmailPausedAsync(bool paused, CancellationToken cancellationToken = default) =>
        _repo.SetSendingPausedAsync(paused, cancellationToken);
}
