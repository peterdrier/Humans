using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="ICityPlanningRepository"/>. The only
/// non-test file that touches <c>DbContext.CampPolygons</c>,
/// <c>DbContext.CampPolygonHistories</c>, or <c>DbContext.CityPlanningSettings</c>
/// after the City Planning migration lands. Uses
/// <see cref="IDbContextFactory{TContext}"/> so the repository can be
/// registered as Singleton while <c>HumansDbContext</c> remains Scoped.
/// </summary>
public sealed class CityPlanningRepository : ICityPlanningRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public CityPlanningRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    // ==========================================================================
    // Reads — CampPolygon
    // ==========================================================================

    public async Task<IReadOnlyList<CampPolygon>> GetPolygonsByCampSeasonIdsAsync(
        IReadOnlyCollection<Guid> campSeasonIds, CancellationToken ct = default)
    {
        if (campSeasonIds.Count == 0)
            return [];

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampPolygons
            .AsNoTracking()
            .Where(p => campSeasonIds.Contains(p.CampSeasonId))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetCampSeasonIdsWithPolygonAsync(
        IReadOnlyCollection<Guid> campSeasonIds, CancellationToken ct = default)
    {
        if (campSeasonIds.Count == 0)
            return [];

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampPolygons
            .AsNoTracking()
            .Where(p => campSeasonIds.Contains(p.CampSeasonId))
            .Select(p => p.CampSeasonId)
            .ToListAsync(ct);
    }

    // ==========================================================================
    // Reads — CampPolygonHistory
    // ==========================================================================

    public async Task<IReadOnlyList<CampPolygonHistory>> GetHistoryForCampSeasonAsync(
        Guid campSeasonId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampPolygonHistories
            .AsNoTracking()
            .Where(h => h.CampSeasonId == campSeasonId)
            .OrderByDescending(h => h.ModifiedAt)
            .ToListAsync(ct);
    }

    public async Task<CampPolygonHistory?> GetHistoryEntryAsync(
        Guid campSeasonId, Guid historyId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CampPolygonHistories
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == historyId && h.CampSeasonId == campSeasonId, ct);
    }

    // ==========================================================================
    // Writes — CampPolygon + CampPolygonHistory
    // ==========================================================================

    public async Task<(CampPolygon polygon, CampPolygonHistory history)> SavePolygonAndAppendHistoryAsync(
        Guid campSeasonId,
        string geoJson,
        double areaSqm,
        Guid modifiedByUserId,
        string note,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var polygon = await ctx.CampPolygons
            .FirstOrDefaultAsync(p => p.CampSeasonId == campSeasonId, ct);

        if (polygon is null)
        {
            polygon = new CampPolygon
            {
                CampSeasonId = campSeasonId,
                GeoJson = geoJson,
                AreaSqm = areaSqm,
                LastModifiedByUserId = modifiedByUserId,
                LastModifiedAt = now,
            };
            ctx.CampPolygons.Add(polygon);
        }
        else
        {
            polygon.GeoJson = geoJson;
            polygon.AreaSqm = areaSqm;
            polygon.LastModifiedByUserId = modifiedByUserId;
            polygon.LastModifiedAt = now;
        }

        var history = new CampPolygonHistory
        {
            CampSeasonId = campSeasonId,
            GeoJson = geoJson,
            AreaSqm = areaSqm,
            ModifiedByUserId = modifiedByUserId,
            ModifiedAt = now,
            Note = note,
        };
        ctx.CampPolygonHistories.Add(history);

        await ctx.SaveChangesAsync(ct);

        // Detach so callers cannot accidentally mutate through a disposed context.
        ctx.Entry(polygon).State = EntityState.Detached;
        ctx.Entry(history).State = EntityState.Detached;
        return (polygon, history);
    }

    // ==========================================================================
    // Reads / Writes — CityPlanningSettings
    // ==========================================================================

    public async Task<CityPlanningSettings?> GetSettingsByYearAsync(
        int year, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.CityPlanningSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Year == year, ct);
    }

    public async Task<CityPlanningSettings> GetOrCreateSettingsAsync(
        int year, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var settings = await ctx.CityPlanningSettings
            .FirstOrDefaultAsync(s => s.Year == year, ct);

        if (settings is null)
        {
            settings = new CityPlanningSettings
            {
                Year = year,
                IsPlacementOpen = false,
                UpdatedAt = now,
            };
            ctx.CityPlanningSettings.Add(settings);
            await ctx.SaveChangesAsync(ct);
        }

        ctx.Entry(settings).State = EntityState.Detached;
        return settings;
    }

    public async Task<CityPlanningSettings> MutateSettingsAsync(
        int year,
        Action<CityPlanningSettings> mutate,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var settings = await ctx.CityPlanningSettings
            .FirstOrDefaultAsync(s => s.Year == year, ct);

        if (settings is null)
        {
            settings = new CityPlanningSettings
            {
                Year = year,
                IsPlacementOpen = false,
                UpdatedAt = now,
            };
            ctx.CityPlanningSettings.Add(settings);
        }

        mutate(settings);
        settings.UpdatedAt = now;
        await ctx.SaveChangesAsync(ct);

        ctx.Entry(settings).State = EntityState.Detached;
        return settings;
    }
}
