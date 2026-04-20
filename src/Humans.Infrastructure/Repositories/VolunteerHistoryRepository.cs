using Microsoft.EntityFrameworkCore;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="IVolunteerHistoryRepository"/>. The only
/// non-test file that touches <c>DbContext.VolunteerHistoryEntries</c> after the
/// Profile migration lands.
/// Uses <see cref="IDbContextFactory{TContext}"/> so the repository can be
/// registered as Singleton while <c>HumansDbContext</c> remains Scoped.
/// </summary>
public sealed class VolunteerHistoryRepository : IVolunteerHistoryRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public VolunteerHistoryRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<VolunteerHistoryEntry>> GetByProfileIdReadOnlyAsync(
        Guid profileId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.VolunteerHistoryEntries
            .AsNoTracking()
            .Where(v => v.ProfileId == profileId)
            .OrderByDescending(v => v.Date)
            .ThenByDescending(v => v.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<VolunteerHistoryEntry>> GetByProfileIdForMutationAsync(
        Guid profileId, CancellationToken ct = default)
    {
        // With IDbContextFactory the context is short-lived, so returned entities
        // are detached. Callers must pass mutated entities explicitly to BatchSaveAsync.
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.VolunteerHistoryEntries
            .AsNoTracking()
            .Where(v => v.ProfileId == profileId)
            .ToListAsync(ct);
    }

    public async Task BatchSaveAsync(
        IReadOnlyList<VolunteerHistoryEntry> toAdd,
        IReadOnlyList<VolunteerHistoryEntry> toUpdate,
        IReadOnlyList<VolunteerHistoryEntry> toRemove,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        if (toRemove.Count > 0)
            ctx.VolunteerHistoryEntries.RemoveRange(toRemove);
        if (toUpdate.Count > 0)
            ctx.VolunteerHistoryEntries.UpdateRange(toUpdate);
        if (toAdd.Count > 0)
            ctx.VolunteerHistoryEntries.AddRange(toAdd);
        await ctx.SaveChangesAsync(ct);
    }
}
