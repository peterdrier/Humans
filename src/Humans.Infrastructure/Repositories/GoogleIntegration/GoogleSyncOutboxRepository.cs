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
public sealed class GoogleSyncOutboxRepository : IGoogleSyncOutboxRepository
{
    private const int LastErrorMaxLength = 4000;

    private readonly IDbContextFactory<HumansDbContext> _factory;

    public GoogleSyncOutboxRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<int> CountFailedAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.GoogleSyncOutboxEvents
            .AsNoTracking()
            .CountAsync(e => e.ProcessedAt == null && e.LastError != null, ct);
    }

    public async Task<int> CountPendingAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.GoogleSyncOutboxEvents
            .AsNoTracking()
            .CountAsync(e => e.ProcessedAt == null, ct);
    }

    public async Task<int> CountStaleAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.GoogleSyncOutboxEvents
            .AsNoTracking()
            .CountAsync(
                e => e.ProcessedAt == null && e.LastError != null && !e.FailedPermanently,
                ct);
    }

    public async Task<int> CountTransientRetriesAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.GoogleSyncOutboxEvents
            .AsNoTracking()
            .CountAsync(
                e => e.ProcessedAt == null && !e.FailedPermanently && e.RetryCount > 0,
                ct);
    }

    public async Task<IReadOnlyList<GoogleSyncOutboxEvent>> GetRecentAsync(
        int take, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.GoogleSyncOutboxEvents
            .AsNoTracking()
            .OrderByDescending(e => e.OccurredAt)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<GoogleSyncOutboxEvent>> GetProcessingBatchAsync(
        int batchSize, int maxRetryCount, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.GoogleSyncOutboxEvents
            .AsNoTracking()
            .Where(e => e.ProcessedAt == null
                && !e.FailedPermanently
                && e.RetryCount < maxRetryCount)
            .OrderBy(e => e.OccurredAt)
            .Take(batchSize)
            .ToListAsync(ct);
    }

    public async Task MarkProcessedAsync(Guid id, Instant processedAt, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
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
        await using var ctx = await _factory.CreateDbContextAsync(ct);
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
        await using var ctx = await _factory.CreateDbContextAsync(ct);
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
