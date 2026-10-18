using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.GoogleIntegration.Data;

/// <summary>
/// EF-backed <see cref="IGoogleSyncLogRepository"/>. Singleton over
/// <see cref="IDbContextFactory{TContext}"/> per design-rules §15b.
/// </summary>
internal sealed class GoogleSyncLogRepository(IDbContextFactory<GoogleIntegrationDbContext> factory)
    : IGoogleSyncLogRepository
{
    /// <summary>Cap on rows returned.</summary>
    private const int MaxRows = 200;

    public async Task AddAsync(GoogleSyncLogEntry entry, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.GoogleSyncLog.Add(entry);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task AddRangeAsync(IReadOnlyCollection<GoogleSyncLogEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0)
            return;

        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.GoogleSyncLog.AddRange(entries);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlySet<Guid>> GetExistingIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return new HashSet<Guid>();

        await using var ctx = await factory.CreateDbContextAsync(ct);
        var found = await ctx.GoogleSyncLog
            .AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .Select(e => e.Id)
            .ToListAsync(ct);
        return new HashSet<Guid>(found);
    }

    public async Task<IReadOnlyList<GoogleSyncLogEntry>> GetByResourceAsync(
        Guid resourceId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.GoogleSyncLog
            .AsNoTracking()
            .Where(e => e.ResourceId == resourceId)
            .OrderByDescending(e => e.OccurredAt) // arch:db-sort-ok top-N selector
            .Take(MaxRows)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<GoogleSyncLogEntry>> GetRecentAsync(
        Instant? from, Instant? to, GoogleSyncLogAction? action, GoogleSyncSource? source, bool? success,
        IReadOnlyCollection<Guid>? resourceIds, int limit, CancellationToken ct = default,
        string? unlinkedDescriptionContains = null)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var query = ctx.GoogleSyncLog.AsNoTracking();
        if (unlinkedDescriptionContains is not null)
        {
            var needle = unlinkedDescriptionContains.ToLowerInvariant();
            var ids = resourceIds ?? [];
#pragma warning disable MA0011 // EF LINQ: ToLower() translates to SQL lower()
            query = query.Where(e => ids.Contains(e.ResourceId)
                || (e.ResourceId == Guid.Empty && e.Description.ToLower().Contains(needle)));
#pragma warning restore MA0011
        }
        else if (resourceIds is not null) query = query.Where(e => resourceIds.Contains(e.ResourceId));
        if (from.HasValue) query = query.Where(e => e.OccurredAt >= from.Value);
        if (to.HasValue) query = query.Where(e => e.OccurredAt < to.Value);
        if (action.HasValue) query = query.Where(e => e.Action == action.Value);
        if (source.HasValue) query = query.Where(e => e.Source == source.Value);
        if (success.HasValue) query = query.Where(e => e.Success == success.Value);
        return await query
            .OrderByDescending(e => e.OccurredAt) // arch:db-sort-ok top-N selector
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetResourceIdsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.GoogleSyncLog.AsNoTracking().Select(e => e.ResourceId).Distinct().ToListAsync(ct);
    }

    public async Task<IReadOnlyList<GoogleSyncLogEntry>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return [];

        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.GoogleSyncLog
            .AsNoTracking()
            .Where(e => e.UserId.HasValue && userIds.Contains(e.UserId.Value))
            .OrderByDescending(e => e.OccurredAt) // arch:db-sort-ok top-N selector
            .Take(MaxRows)
            .ToListAsync(ct);
    }

    public async Task<int> DeleteByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return 0;

        await using var ctx = await factory.CreateDbContextAsync(ct);
        var rows = await ctx.GoogleSyncLog
            .Where(e => e.UserId.HasValue && userIds.Contains(e.UserId.Value))
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;

        ctx.GoogleSyncLog.RemoveRange(rows);
        await ctx.SaveChangesAsync(ct);
        return rows.Count;
    }

    public async Task<IReadOnlyList<GoogleSyncLogEntry>> GetAllByUserIdsContributorAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return [];

        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.GoogleSyncLog
            .AsNoTracking()
            .Where(e => e.UserId.HasValue && userIds.Contains(e.UserId.Value))
            .OrderByDescending(e => e.OccurredAt) // arch:db-sort-ok export ordering
            .ToListAsync(ct);
    }
}
