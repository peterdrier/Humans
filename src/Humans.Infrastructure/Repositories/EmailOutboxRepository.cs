using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="IEmailOutboxRepository"/>. The only
/// non-test file that touches <c>DbContext.EmailOutboxMessages</c> after the
/// Email §15 migration lands — plus the single <c>IsEmailSendingPaused</c>
/// row in <c>system_settings</c> which is the Email section's pause flag.
/// Uses <see cref="IDbContextFactory{TContext}"/> so the repository is
/// registered as Singleton while <c>HumansDbContext</c> stays Scoped.
/// </summary>
public sealed class EmailOutboxRepository : IEmailOutboxRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public EmailOutboxRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    // ==========================================================================
    // Reads — admin dashboard and profile views
    // ==========================================================================

    public async Task<int> GetTotalCountAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.EmailOutboxMessages.CountAsync(ct);
    }

    public async Task<int> GetCountByStatusAsync(EmailOutboxStatus status, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.EmailOutboxMessages.CountAsync(m => m.Status == status, ct);
    }

    public async Task<int> GetSentCountSinceAsync(Instant since, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.EmailOutboxMessages
            .CountAsync(m => m.Status == EmailOutboxStatus.Sent && m.SentAt > since, ct);
    }

    public async Task<IReadOnlyList<EmailOutboxMessage>> GetRecentAsync(
        int take, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.EmailOutboxMessages
            .AsNoTracking()
            .OrderByDescending(m => m.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<EmailOutboxMessage>> GetForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.EmailOutboxMessages
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<int> GetCountForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.EmailOutboxMessages.CountAsync(m => m.UserId == userId, ct);
    }

    public async Task<int> GetPendingCountAsync(int maxRetries, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.EmailOutboxMessages
            .CountAsync(m => m.SentAt == null && m.RetryCount < maxRetries, ct);
    }

    // ==========================================================================
    // Writes — admin operations
    // ==========================================================================

    public async Task AddAsync(EmailOutboxMessage message, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.EmailOutboxMessages.Add(message);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<string?> RetryAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
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
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var message = await ctx.EmailOutboxMessages.FindAsync([id], ct);
        if (message is null) return null;

        var recipient = message.RecipientEmail;
        ctx.EmailOutboxMessages.Remove(message);
        await ctx.SaveChangesAsync(ct);

        return recipient;
    }

    // ==========================================================================
    // Processor — used by ProcessEmailOutboxJob
    // ==========================================================================

    public async Task<IReadOnlyList<EmailOutboxMessage>> GetProcessingBatchAsync(
        Instant now,
        Instant staleThreshold,
        int maxRetries,
        int batchSize,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.EmailOutboxMessages
            .AsNoTracking()
            .Where(m => m.SentAt == null
                && m.RetryCount < maxRetries
                && (m.NextRetryAt == null || m.NextRetryAt <= now)
                && (m.PickedUpAt == null || m.PickedUpAt < staleThreshold))
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);
    }

    public async Task MarkPickedUpAsync(
        IReadOnlyCollection<Guid> messageIds, Instant pickedUpAt, CancellationToken ct = default)
    {
        if (messageIds.Count == 0) return;

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        // Load-mutate-save rather than ExecuteUpdateAsync so unit tests using
        // the EF InMemory provider still see the update. At ~500-user scale the
        // batch is at most OutboxBatchSize rows.
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
        await using var ctx = await _factory.CreateDbContextAsync(ct);
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
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var message = await ctx.EmailOutboxMessages.FindAsync([messageId], ct);
        if (message is null) return false;

        message.Status = EmailOutboxStatus.Failed;
        message.RetryCount += 1;
        message.LastError = lastError.Length > 4000 ? lastError[..4000] : lastError;
        message.NextRetryAt = nextRetryAt;
        message.PickedUpAt = null;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    // ==========================================================================
    // Cleanup — used by CleanupEmailOutboxJob
    // ==========================================================================

    public async Task<int> DeleteSentOlderThanAsync(Instant cutoff, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        // Load-then-RemoveRange so unit tests using the EF InMemory provider
        // still cover the path. ExecuteDeleteAsync would be cheaper at scale
        // but is not supported by the InMemory provider.
        var toDelete = await ctx.EmailOutboxMessages
            .Where(m => m.Status == EmailOutboxStatus.Sent && m.SentAt < cutoff)
            .ToListAsync(ct);
        if (toDelete.Count == 0) return 0;
        ctx.EmailOutboxMessages.RemoveRange(toDelete);
        await ctx.SaveChangesAsync(ct);
        return toDelete.Count;
    }

    // ==========================================================================
    // Pause flag — IsEmailSendingPaused row in system_settings
    // ==========================================================================

    public async Task<bool> GetSendingPausedAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var setting = await ctx.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == SystemSettingKeys.IsEmailSendingPaused, ct);
        return string.Equals(setting?.Value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task SetSendingPausedAsync(bool paused, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var setting = await ctx.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == SystemSettingKeys.IsEmailSendingPaused, ct);
        if (setting is null)
        {
            setting = new SystemSetting
            {
                Key = SystemSettingKeys.IsEmailSendingPaused,
                Value = paused ? "true" : "false",
            };
            ctx.SystemSettings.Add(setting);
        }
        else
        {
            setting.Value = paused ? "true" : "false";
        }

        await ctx.SaveChangesAsync(ct);
    }
}
