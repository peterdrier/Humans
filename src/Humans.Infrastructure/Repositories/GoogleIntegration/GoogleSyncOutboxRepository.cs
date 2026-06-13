using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.GoogleIntegration;

/// <summary>
/// EF-backed implementation of <see cref="IGoogleSyncOutboxRepository"/>.
/// Registered as Singleton via <see cref="IDbContextFactory{TContext}"/>
/// per design-rules §15b — every method creates and disposes a fresh
/// short-lived <see cref="HumansDbContext"/>.
/// </summary>
internal sealed class GoogleSyncOutboxRepository(IDbContextFactory<HumansDbContext> factory)
    : IGoogleSyncOutboxRepository
{
    private const int LastErrorMaxLength = 4000;

    public async Task AddAsync(GoogleSyncOutboxEvent outboxEvent, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.GoogleSyncOutboxEvents.Add(outboxEvent);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task AddRangeAsync(
        IReadOnlyCollection<GoogleSyncOutboxEvent> outboxEvents,
        CancellationToken ct = default)
    {
        if (outboxEvents.Count == 0)
            return;

        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.GoogleSyncOutboxEvents.AddRange(outboxEvents);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<int> CountFailedAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        // Include permanently-failed events (FailedPermanently=true) as well as
        // transient-retry events (ProcessedAt=null && LastError!=null).
        // Previously only the latter was counted, so dead-lettered events silently
        // fell out of the badge to zero (nobodies-collective/Humans#847).
        return await ctx.GoogleSyncOutboxEvents
            .AsNoTracking()
            .CountAsync(e => e.FailedPermanently || (e.ProcessedAt == null && e.LastError != null), ct);
    }

    public async Task<int> CountPendingAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.GoogleSyncOutboxEvents
            .AsNoTracking()
            .CountAsync(e => e.ProcessedAt == null, ct);
    }

    public async Task<IReadOnlyList<GoogleSyncOutboxEvent>> GetRecentAsync(
        int take, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.GoogleSyncOutboxEvents
            .AsNoTracking()
            .OrderByDescending(e => e.OccurredAt) // arch:db-sort-ok top-N recent-events selector (Take)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<GoogleSyncOutboxEvent>> GetProcessingBatchAsync(
        int batchSize, int maxRetryCount, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.GoogleSyncOutboxEvents
            .AsNoTracking()
            .Where(e => e.ProcessedAt == null
                && !e.FailedPermanently
                && e.RetryCount < maxRetryCount)
            .OrderBy(e => e.OccurredAt) // arch:db-sort-ok outbox FIFO processing batch (Take)
            .Take(batchSize)
            .ToListAsync(ct);
    }

    public async Task<bool> RequeueAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var entity = await ctx.GoogleSyncOutboxEvents.FindAsync([id], ct);
        if (entity is null || !entity.FailedPermanently)
            return false;

        entity.FailedPermanently = false;
        entity.ProcessedAt = null;
        entity.RetryCount = 0;
        entity.LastError = null;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> RequeueAllFailedAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var failed = await ctx.GoogleSyncOutboxEvents
            .Where(e => e.FailedPermanently)
            .ToListAsync(ct);

        foreach (var entity in failed)
        {
            entity.FailedPermanently = false;
            entity.ProcessedAt = null;
            entity.RetryCount = 0;
            entity.LastError = null;
        }

        await ctx.SaveChangesAsync(ct);
        return failed.Count;
    }

    public async Task MarkProcessedAsync(Guid id, Instant processedAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var entity = await ctx.GoogleSyncOutboxEvents.FindAsync([id], ct);
        if (entity is null)
            return;

        entity.ProcessedAt = processedAt;
        entity.LastError = null;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task MarkPermanentlyFailedAsync(
        Guid id, Instant processedAt, string lastError, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var entity = await ctx.GoogleSyncOutboxEvents.FindAsync([id], ct);
        if (entity is null)
            return;

        entity.FailedPermanently = true;
        entity.ProcessedAt = processedAt;
        entity.LastError = Truncate(lastError);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<(bool ExhaustedRetries, int RetryCount)> IncrementRetryAsync(
        Guid id,
        Instant processedAt,
        string lastError,
        int maxRetryCount,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var entity = await ctx.GoogleSyncOutboxEvents.FindAsync([id], ct);
        if (entity is null)
            return (false, 0);

        entity.RetryCount += 1;
        entity.LastError = Truncate(lastError);

        var exhausted = entity.RetryCount >= maxRetryCount;
        if (exhausted)
        {
            entity.FailedPermanently = true;
            entity.ProcessedAt = processedAt;
        }

        await ctx.SaveChangesAsync(ct);
        return (exhausted, entity.RetryCount);
    }

    private static string Truncate(string value)
        => value.Length > LastErrorMaxLength ? value[..LastErrorMaxLength] : value;
}
