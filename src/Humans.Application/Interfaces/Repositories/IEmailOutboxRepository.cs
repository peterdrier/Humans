using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the <c>email_outbox_messages</c> table plus the
/// <c>IsEmailSendingPaused</c> row of the <c>system_settings</c> table
/// (co-located here because the pause flag is the Email section's
/// operational state). The only non-test file that writes to the outbox
/// DbSet after the Email §15 migration lands.
/// </summary>
/// <remarks>
/// Uses <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{TContext}"/>
/// so the repository can be registered as Singleton while
/// <c>HumansDbContext</c> remains Scoped. Each method creates and disposes
/// its own short-lived context.
/// </remarks>
public interface IEmailOutboxRepository
{
    // ==========================================================================
    // Reads — admin dashboard and profile views
    // ==========================================================================

    /// <summary>
    /// Returns the total number of outbox rows.
    /// </summary>
    Task<int> GetTotalCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the number of rows with a given <see cref="EmailOutboxStatus"/>.
    /// </summary>
    Task<int> GetCountByStatusAsync(EmailOutboxStatus status, CancellationToken ct = default);

    /// <summary>
    /// Returns the number of messages sent after <paramref name="since"/>.
    /// </summary>
    Task<int> GetSentCountSinceAsync(Instant since, CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent messages (by <c>CreatedAt</c> descending),
    /// read-only. <paramref name="take"/> is clamped by the caller.
    /// </summary>
    Task<IReadOnlyList<EmailOutboxMessage>> GetRecentAsync(
        int take, CancellationToken ct = default);

    /// <summary>
    /// Returns all outbox messages for a user, read-only, ordered by
    /// <c>CreatedAt</c> descending.
    /// </summary>
    Task<IReadOnlyList<EmailOutboxMessage>> GetForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the outbox-row count for a single user.
    /// </summary>
    Task<int> GetCountForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the number of messages that are not yet <c>Sent</c> and are
    /// still below the retry cap — used to populate the Prometheus outbox
    /// gauge after each processor batch.
    /// </summary>
    Task<int> GetPendingCountAsync(int maxRetries, CancellationToken ct = default);

    // ==========================================================================
    // Writes — admin operations
    // ==========================================================================

    /// <summary>
    /// Enqueues a new message. The caller builds the entity (including
    /// <see cref="EmailOutboxMessage.Status"/> and <see cref="EmailOutboxMessage.CreatedAt"/>);
    /// the repository is just the persistence gate.
    /// </summary>
    Task AddAsync(EmailOutboxMessage message, CancellationToken ct = default);

    /// <summary>
    /// Resets a message back to <see cref="EmailOutboxStatus.Queued"/> so the
    /// processor will pick it up again. Returns the message's recipient email
    /// on success, or <c>null</c> if the message does not exist.
    /// </summary>
    Task<string?> RetryAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Deletes a single outbox message. Returns the message's recipient email
    /// on success, or <c>null</c> if the message does not exist.
    /// </summary>
    Task<string?> DiscardAsync(Guid id, CancellationToken ct = default);

    // ==========================================================================
    // Processor — used by ProcessEmailOutboxJob
    // ==========================================================================

    /// <summary>
    /// Returns up to <paramref name="batchSize"/> messages ready for the
    /// processor: not yet sent, below the retry cap, past
    /// <see cref="EmailOutboxMessage.NextRetryAt"/>, and either never
    /// picked up or stale (older than <paramref name="staleThreshold"/>).
    /// Returned entities are detached; to update, use
    /// <see cref="MarkPickedUpAsync"/> / <see cref="MarkSentAsync"/> /
    /// <see cref="MarkFailedAsync"/>.
    /// </summary>
    Task<IReadOnlyList<EmailOutboxMessage>> GetProcessingBatchAsync(
        Instant now,
        Instant staleThreshold,
        int maxRetries,
        int batchSize,
        CancellationToken ct = default);

    /// <summary>
    /// Marks the given message ids as picked up (sets
    /// <see cref="EmailOutboxMessage.PickedUpAt"/>) in a single round-trip.
    /// </summary>
    Task MarkPickedUpAsync(
        IReadOnlyCollection<Guid> messageIds, Instant pickedUpAt, CancellationToken ct = default);

    /// <summary>
    /// Marks a single message as sent (Status=Sent, SentAt=now, PickedUpAt=null).
    /// Returns <c>true</c> if the message existed.
    /// </summary>
    Task<bool> MarkSentAsync(Guid messageId, Instant sentAt, CancellationToken ct = default);

    /// <summary>
    /// Marks a single message as failed, incrementing <c>RetryCount</c> and
    /// setting <c>NextRetryAt</c> / <c>LastError</c> / <c>PickedUpAt=null</c>.
    /// Returns <c>true</c> if the message existed.
    /// </summary>
    Task<bool> MarkFailedAsync(
        Guid messageId,
        Instant now,
        string lastError,
        Instant nextRetryAt,
        CancellationToken ct = default);

    // ==========================================================================
    // Cleanup — used by CleanupEmailOutboxJob
    // ==========================================================================

    /// <summary>
    /// Deletes every <see cref="EmailOutboxStatus.Sent"/> message with a
    /// <see cref="EmailOutboxMessage.SentAt"/> strictly earlier than
    /// <paramref name="cutoff"/>. Returns the number of rows deleted.
    /// </summary>
    Task<int> DeleteSentOlderThanAsync(Instant cutoff, CancellationToken ct = default);

    // ==========================================================================
    // Pause flag — IsEmailSendingPaused row in system_settings
    // ==========================================================================

    /// <summary>
    /// Returns whether the <c>IsEmailSendingPaused</c> flag is set to "true"
    /// (case-insensitive). Absent rows read as <c>false</c>.
    /// </summary>
    Task<bool> GetSendingPausedAsync(CancellationToken ct = default);

    /// <summary>
    /// Sets the <c>IsEmailSendingPaused</c> flag, upserting the row if it
    /// does not already exist.
    /// </summary>
    Task SetSendingPausedAsync(bool paused, CancellationToken ct = default);
}
