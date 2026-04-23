using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Repositories.GoogleIntegration;

/// <summary>
/// EF-backed implementation of <see cref="IGoogleSyncOutboxRepository"/>.
/// Registered as Singleton via <see cref="IDbContextFactory{TContext}"/>
/// per design-rules §15b — every method creates and disposes a fresh
/// short-lived <see cref="HumansDbContext"/>.
/// </summary>
public sealed class GoogleSyncOutboxRepository : IGoogleSyncOutboxRepository
{
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
}
