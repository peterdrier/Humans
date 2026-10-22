using Humans.CityPlanning.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.CityPlanning.Data;

internal sealed class CityPlanningRepository(IDbContextFactory<CityPlanningDbContext> factory) : ICityPlanningRepository
{
    public async Task<IReadOnlyList<CampPolygon>> GetPolygonsByCampSeasonIdsAsync(
        IReadOnlyCollection<Guid> campSeasonIds, CancellationToken ct = default)
    {
        if (campSeasonIds.Count == 0)
        {
            return [];
        }

        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CampPolygons
            .AsNoTracking()
            .Where(p => campSeasonIds.Contains(p.CampSeasonId))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetCampSeasonIdsWithPolygonAsync(
        IReadOnlyCollection<Guid> campSeasonIds, CancellationToken ct = default)
    {
        if (campSeasonIds.Count == 0)
        {
            return [];
        }

        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CampPolygons
            .AsNoTracking()
            .Where(p => campSeasonIds.Contains(p.CampSeasonId))
            .Select(p => p.CampSeasonId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CampPolygonHistory>> GetHistoryForCampSeasonAsync(
        Guid campSeasonId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        // Unordered: the controller sorts for display.
        return await ctx.CampPolygonHistories
            .AsNoTracking()
            .Where(h => h.CampSeasonId == campSeasonId)
            .ToListAsync(ct);
    }

    public async Task<CampPolygonHistory?> GetHistoryEntryAsync(
        Guid campSeasonId, Guid historyId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CampPolygonHistories
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == historyId && h.CampSeasonId == campSeasonId, ct);
    }

    public async Task<CampPolygon> SavePolygonAndAppendHistoryAsync(
        Guid campSeasonId,
        string geoJson,
        double areaSqm,
        Guid modifiedByUserId,
        string note,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

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

        // Detach so callers cannot mutate through a disposed context.
        ctx.Entry(polygon).State = EntityState.Detached;
        return polygon;
    }

    public async Task<int> DeletePolygonsForCampSeasonsAsync(
        IReadOnlyCollection<Guid> campSeasonIds, CancellationToken ct = default)
    {
        if (campSeasonIds.Count == 0)
        {
            return 0;
        }

        await using var ctx = await factory.CreateDbContextAsync(ct);

        // Load-and-remove rather than ExecuteDelete keeps this provider-agnostic for the test suite.
        var history = await ctx.CampPolygonHistories
            .Where(h => campSeasonIds.Contains(h.CampSeasonId))
            .ToListAsync(ct);
        ctx.CampPolygonHistories.RemoveRange(history);

        var polygons = await ctx.CampPolygons
            .Where(p => campSeasonIds.Contains(p.CampSeasonId))
            .ToListAsync(ct);
        ctx.CampPolygons.RemoveRange(polygons);

        if (history.Count + polygons.Count == 0)
        {
            return 0;
        }

        await ctx.SaveChangesAsync(ct);
        return history.Count + polygons.Count;
    }

    public async Task<CityPlanningSettings?> GetSettingsAsync(int year, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.CityPlanningSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Year == year, ct);
    }

    public async Task<CityPlanningSettings> GetOrCreateSettingsAsync(
        int year, Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var settings = await ctx.CityPlanningSettings
            .FirstOrDefaultAsync(s => s.Year == year, ct);

        if (settings is null)
        {
            settings = new CityPlanningSettings
            {
                Year = year,
                UpdatedAt = now,
            };
            ctx.CityPlanningSettings.Add(settings);
            await ctx.SaveChangesAsync(ct);
        }

        ctx.Entry(settings).State = EntityState.Detached;
        return settings;
    }

    public async Task MutateSettingsAsync(
        int year,
        Action<CityPlanningSettings> mutate,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var settings = await ctx.CityPlanningSettings
            .FirstOrDefaultAsync(s => s.Year == year, ct);

        if (settings is null)
        {
            settings = new CityPlanningSettings
            {
                Year = year,
                UpdatedAt = now,
            };
            ctx.CityPlanningSettings.Add(settings);
        }

        mutate(settings);
        settings.UpdatedAt = now;
        await ctx.SaveChangesAsync(ct);
    }
}
