using Humans.GoogleIntegration.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.GoogleIntegration.Data;

/// <summary>
/// EF-backed <see cref="IGoogleSyncLogRepository"/>. Singleton over
/// <see cref="IDbContextFactory{TContext}"/> per design-rules §15b.
/// </summary>
internal sealed class GoogleSyncLogRepository(IDbContextFactory<GoogleIntegrationDbContext> factory)
    : IGoogleSyncLogRepository
{
    /// <summary>Matches the cap the audit-backed sync pages used before nobodies-collective/Humans#1083.</summary>
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
