using Humans.Email.Domain;
using Humans.Base.Enums;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace Humans.Email.Data;

internal sealed class EmailOutboxRepository(IDbContextFactory<EmailDbContext> factory) : IEmailOutboxRepository
{
    public async Task<int> GetTotalCountAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EmailOutboxMessages.CountAsync(ct);
    }

    public async Task<int> GetCountByStatusAsync(EmailOutboxStatus status, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EmailOutboxMessages.CountAsync(m => m.Status == status, ct);
    }

    public async Task<int> GetSentCountSinceAsync(Instant since, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EmailOutboxMessages
            .CountAsync(m => m.Status == EmailOutboxStatus.Sent && m.SentAt > since, ct);
    }

    public async Task<IReadOnlyList<EmailOutboxMessage>> GetRecentAsync(int take, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EmailOutboxMessages
            .AsNoTracking()
            .OrderByDescending(m => m.CreatedAt) // arch:db-sort-ok top-N selector
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<EmailOutboxMessage>> GetForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EmailOutboxMessages
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderByDescending(m => m.CreatedAt) // arch:db-sort-ok operational chronology
            .ToListAsync(ct);
    }

    public async Task<int> GetCountForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EmailOutboxMessages.CountAsync(m => m.UserId == userId, ct);
    }

    public async Task<int> GetPendingCountAsync(int maxRetries, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EmailOutboxMessages
            .CountAsync(m => m.SentAt == null && m.RetryCount < maxRetries, ct);
    }

    public async Task<bool> AddAsync(EmailOutboxMessage message, CancellationToken ct = default, bool idempotent = false)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        if (idempotent && await ctx.EmailOutboxMessages.AnyAsync(m => m.Id == message.Id, ct))
            return false;
        ctx.EmailOutboxMessages.Add(message);
        try
        {
            await ctx.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (idempotent && ex.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_email_outbox_messages" })
        {
            // Another handoff of this logical delivery won the primary-key insert.
            return false;
        }
    }

    public async Task<string?> RetryAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var message = await ctx.EmailOutboxMessages.FindAsync([id], ct);
        if (message is null) return null;

        message.Status = EmailOutboxStatus.Queued;
        message.RetryCount = 0;
        message.LastError = null;
        message.NextRetryAt = null;
        message.PickedUpAt = null;
        await ctx.SaveChangesAsync(ct);

        return message.RecipientEmail;
    }

    public async Task<string?> DiscardAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var message = await ctx.EmailOutboxMessages.FindAsync([id], ct);
        if (message is null) return null;

        var recipient = message.RecipientEmail;
        ctx.EmailOutboxMessages.Remove(message);
        await ctx.SaveChangesAsync(ct);

        return recipient;
    }

    public async Task<IReadOnlyList<EmailOutboxMessage>> GetProcessingBatchAsync(
        Instant now,
        Instant staleThreshold,
        int maxRetries,
        int batchSize,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EmailOutboxMessages
            .AsNoTracking()
            .Where(m => m.SentAt == null
                && m.RetryCount < maxRetries
                && (m.NextRetryAt == null || m.NextRetryAt <= now)
                && (m.PickedUpAt == null || m.PickedUpAt < staleThreshold))
            // Urgent mail jumps the queue: a member is waiting on it, and bulk mail drains at one send per
            // second. FIFO still holds within each class.
            .OrderBy(m => m.Urgent == true ? 0 : 1) // arch:db-sort-ok outbox priority claim batch (Take)
            .ThenBy(m => m.CreatedAt) // arch:db-sort-ok outbox FIFO within priority class (Take)
            .Take(batchSize)
            .ToListAsync(ct);
    }

    public async Task MarkPickedUpAsync(
        IReadOnlyCollection<Guid> messageIds, Instant pickedUpAt, CancellationToken ct = default)
    {
        if (messageIds.Count == 0) return;

        await using var ctx = await factory.CreateDbContextAsync(ct);
        // Load-mutate-save rather than ExecuteUpdateAsync so the EF InMemory tests see the update.
        var messages = await ctx.EmailOutboxMessages
            .Where(m => messageIds.Contains(m.Id))
            .ToListAsync(ct);
        foreach (var message in messages)
        {
            message.PickedUpAt = pickedUpAt;
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> MarkSentAsync(Guid messageId, Instant sentAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var message = await ctx.EmailOutboxMessages.FindAsync([messageId], ct);
        if (message is null) return false;

        message.Status = EmailOutboxStatus.Sent;
        message.SentAt = sentAt;
        message.PickedUpAt = null;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> MarkFailedAsync(
        Guid messageId,
        Instant now,
        string lastError,
        Instant nextRetryAt,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var message = await ctx.EmailOutboxMessages.FindAsync([messageId], ct);
        if (message is null) return false;

        message.Status = EmailOutboxStatus.Failed;
        message.RetryCount += 1;
        if (lastError.Length > 4000)
        {
            var length = 4000;
            if (char.IsHighSurrogate(lastError[length - 1]) && char.IsLowSurrogate(lastError[length]))
                length--;
            lastError = lastError[..length];
        }
        message.LastError = lastError;
        message.NextRetryAt = nextRetryAt;
        message.PickedUpAt = null;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> DeleteSentOlderThanAsync(Instant cutoff, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        // Load-then-RemoveRange: the EF InMemory provider used by the tests has no ExecuteDeleteAsync.
        var toDelete = await ctx.EmailOutboxMessages
            .Where(m => m.Status == EmailOutboxStatus.Sent && m.SentAt < cutoff)
            .ToListAsync(ct);
        if (toDelete.Count == 0) return 0;
        ctx.EmailOutboxMessages.RemoveRange(toDelete);
        await ctx.SaveChangesAsync(ct);
        return toDelete.Count;
    }

    public async Task<int> DeleteForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        // Load-then-RemoveRange for the same InMemory reason.
        var toDelete = await ctx.EmailOutboxMessages
            .Where(m => m.UserId == userId)
            .ToListAsync(ct);
        if (toDelete.Count == 0) return 0;
        ctx.EmailOutboxMessages.RemoveRange(toDelete);
        await ctx.SaveChangesAsync(ct);
        return toDelete.Count;
    }

    public async Task ReassignToUserAsync(Guid sourceUserId, Guid targetUserId, CancellationToken ct = default)
    {
        if (sourceUserId == targetUserId) return;

        await using var ctx = await factory.CreateDbContextAsync(ct);
        var messages = await ctx.EmailOutboxMessages
            .Where(m => m.UserId == sourceUserId)
            .ToListAsync(ct);
        if (messages.Count == 0) return;

        foreach (var message in messages)
            message.UserId = targetUserId;

        await ctx.SaveChangesAsync(ct);
    }

    public async Task IncrementDailySendCountAsync(
        LocalDate date, string templateName, bool succeeded, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var row = await ctx.EmailDailySendCounts.FindAsync([date, templateName], ct);
        if (row is null)
        {
            row = new EmailDailySendCount { Date = date, TemplateName = templateName };
            ctx.EmailDailySendCounts.Add(row);
        }

        if (succeeded) row.SentCount += 1;
        else row.FailedCount += 1;

        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<EmailDailySendCount>> GetDailySendCountsSinceAsync(
        LocalDate since, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EmailDailySendCounts
            .AsNoTracking()
            .Where(c => c.Date >= since)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlySet<(LocalDate Date, string TemplateName)>> GetDailySendCountKeysAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var rows = await ctx.EmailDailySendCounts
            .AsNoTracking()
            .Select(c => new { c.Date, c.TemplateName })
            .ToListAsync(ct);
        return rows.Select(r => (r.Date, r.TemplateName)).ToHashSet();
    }

    public async Task AddDailySendCountsAsync(
        IReadOnlyList<EmailDailySendCount> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.EmailDailySendCounts.AddRange(rows);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<EmailOutboxMessage>> GetSentSinceAsync(
        Instant since, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EmailOutboxMessages
            .AsNoTracking()
            .Where(m => m.Status == EmailOutboxStatus.Sent && m.SentAt >= since)
            .ToListAsync(ct);
    }
}
