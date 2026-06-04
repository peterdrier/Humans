using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Repositories.Camps;

/// <summary>
/// EF-backed <see cref="IShiftObligationRepository"/>. Touches only
/// <c>shift_obligations</c> and <c>camp_season_shift_obligations</c> — the two
/// tables it owns. Per Camps convention: stateless over
/// <see cref="IDbContextFactory{HumansDbContext}"/>, registered Singleton.
/// </summary>
internal sealed class ShiftObligationRepository : IShiftObligationRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public ShiftObligationRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<ShiftObligation>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ShiftObligations
            .AsNoTracking()
            .OrderBy(o => o.SortOrder) // arch:db-sort-ok — stable config order; service re-derives display order
            .ToListAsync(ct);
    }

    public async Task<ShiftObligation?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.ShiftObligations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id, ct);
    }

    public async Task AddAsync(ShiftObligation obligation, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.ShiftObligations.Add(obligation);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(ShiftObligation obligation, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var existing = await ctx.ShiftObligations.FirstOrDefaultAsync(o => o.Id == obligation.Id, ct);
        if (existing is null)
        {
            return;
        }

        existing.TargetType = obligation.TargetType;
        existing.TargetId = obligation.TargetId;
        existing.CampRoleSlug = obligation.CampRoleSlug;
        existing.Applicability = obligation.Applicability;
        existing.DefaultRequiredShiftCount = obligation.DefaultRequiredShiftCount;
        existing.IsActive = obligation.IsActive;
        existing.SortOrder = obligation.SortOrder;
        existing.UpdatedAt = obligation.UpdatedAt;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<CampSeasonShiftObligation>> GetOverridesForSeasonsAsync(
        IReadOnlyCollection<Guid> campSeasonIds, CancellationToken ct = default)
    {
        if (campSeasonIds.Count == 0)
        {
            return [];
        }

        var ids = campSeasonIds.Distinct().ToList();
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampSeasonShiftObligations
            .AsNoTracking()
            .Where(o => ids.Contains(o.CampSeasonId))
            .ToListAsync(ct);
    }

    public async Task SetOverrideAsync(
        Guid campSeasonId, Guid shiftObligationId, int? requiredShiftCount,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var existing = await ctx.CampSeasonShiftObligations
            .FirstOrDefaultAsync(
                o => o.CampSeasonId == campSeasonId && o.ShiftObligationId == shiftObligationId, ct);

        if (requiredShiftCount is null)
        {
            if (existing is not null)
            {
                ctx.CampSeasonShiftObligations.Remove(existing);
                await ctx.SaveChangesAsync(ct);
            }
            return;
        }

        if (existing is null)
        {
            ctx.CampSeasonShiftObligations.Add(new CampSeasonShiftObligation
            {
                Id = Guid.NewGuid(),
                CampSeasonId = campSeasonId,
                ShiftObligationId = shiftObligationId,
                RequiredShiftCount = requiredShiftCount.Value,
            });
        }
        else
        {
            existing.RequiredShiftCount = requiredShiftCount.Value;
        }

        await ctx.SaveChangesAsync(ct);
    }
}
