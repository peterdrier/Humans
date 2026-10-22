using Humans.Base.Interfaces.Repositories;
using Humans.Email.Contracts;
using Humans.Email.Domain;
using Humans.Base.Enums;
using NodaTime;

namespace Humans.Email.Data;

internal interface IEmailOutboxRepository : IRepository
{
    Task<int> GetTotalCountAsync(CancellationToken ct = default);

    Task<int> GetCountByStatusAsync(EmailOutboxStatus status, CancellationToken ct = default);

    Task<int> GetSentCountSinceAsync(Instant since, CancellationToken ct = default);

    /// <summary>Newest first. Ordering and take stay in the database so the dashboard never loads the whole outbox.</summary>
    Task<IReadOnlyList<EmailOutboxMessage>> GetRecentAsync(int take, CancellationToken ct = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<EmailOutboxMessage>> GetForUserAsync(
        Guid userId, CancellationToken ct = default);

    Task<int> GetCountForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Messages not yet sent and still below the retry cap.</summary>
    Task<int> GetPendingCountAsync(int maxRetries, CancellationToken ct = default);

    /// <summary>
    /// Returns true only for a new row. With <paramref name="idempotent"/>, an existing primary key
    /// counts as an earlier successful handoff: returns false and keeps the stored payload.
    /// </summary>
    Task<bool> AddAsync(EmailOutboxMessage message, CancellationToken ct = default, bool idempotent = false);

    /// <summary>Requeues the message. Returns its recipient address, or null when it doesn't exist.</summary>
    Task<string?> RetryAsync(Guid id, CancellationToken ct = default);

    /// <summary>Returns the deleted message's recipient address, or null when it doesn't exist.</summary>
    Task<string?> DiscardAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Unsent rows below the retry cap, past <see cref="EmailOutboxMessage.NextRetryAt"/>, and never picked up
    /// or picked up before <paramref name="staleThreshold"/>. <see cref="TimeSensitiveTemplates.Names"/> go first
    /// so a bulk backlog can't delay them; FIFO otherwise. Rows are detached: update them through the Mark methods.
    /// </summary>
    Task<IReadOnlyList<EmailOutboxMessage>> GetProcessingBatchAsync(
        Instant now,
        Instant staleThreshold,
        int maxRetries,
        int batchSize,
        CancellationToken ct = default);

    Task MarkPickedUpAsync(
        IReadOnlyCollection<Guid> messageIds, Instant pickedUpAt, CancellationToken ct = default);

    /// <summary>Returns false when the message doesn't exist.</summary>
    Task<bool> MarkSentAsync(Guid messageId, Instant sentAt, CancellationToken ct = default);

    /// <summary>Returns false when the message doesn't exist.</summary>
    Task<bool> MarkFailedAsync(
        Guid messageId,
        Instant now,
        string lastError,
        Instant nextRetryAt,
        CancellationToken ct = default);

    Task<int> DeleteSentOlderThanAsync(Instant cutoff, CancellationToken ct = default);

    /// <summary>GDPR erasure: every status goes, since each row holds the address, name and rendered body.</summary>
    Task<int> DeleteForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Moves every row, whatever its status. Safe to repeat.</summary>
    Task ReassignToUserAsync(Guid sourceUserId, Guid targetUserId, CancellationToken ct = default);

    // Daily send counts outlive the outbox rows: CleanupEmailOutboxJob never purges them,
    // so they stay the denominator for spam-rate spikes.

    Task IncrementDailySendCountAsync(
        LocalDate date, string templateName, bool succeeded, CancellationToken ct = default);

    Task<IReadOnlyList<EmailDailySendCount>> GetDailySendCountsSinceAsync(
        LocalDate since, CancellationToken ct = default);

    /// <summary>The backfill skips these keys: it must never overwrite counts the processor recorded.</summary>
    Task<IReadOnlySet<(LocalDate Date, string TemplateName)>> GetDailySendCountKeysAsync(
        CancellationToken ct = default);

    /// <summary>The caller has already removed keys that exist.</summary>
    Task AddDailySendCountsAsync(
        IReadOnlyList<EmailDailySendCount> rows, CancellationToken ct = default);

    /// <summary>
    /// Sent rows only, filtered on <see cref="EmailOutboxMessage.SentAt"/> to match the retention cutoff.
    /// Failures are counted only by the live processor.
    /// </summary>
    Task<IReadOnlyList<EmailOutboxMessage>> GetSentSinceAsync(
        Instant since, CancellationToken ct = default);
}
