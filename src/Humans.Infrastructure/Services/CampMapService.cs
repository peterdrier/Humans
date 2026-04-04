using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Infrastructure.Services;

public class CampMapService : ICampMapService
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;
    private readonly IOptions<CampMapOptions> _options;

    public CampMapService(HumansDbContext dbContext, IClock clock, IOptions<CampMapOptions> options)
    {
        _dbContext = dbContext;
        _clock = clock;
        _options = options;
    }

    public async Task<List<CampPolygonDto>> GetCampPolygonsAsync(int year, CancellationToken cancellationToken = default)
    {
        return await _dbContext.CampPolygons
            .Include(p => p.CampSeason).ThenInclude(s => s.Camp)
            .Where(p => p.CampSeason.Year == year)
            .Select(p => new CampPolygonDto(
                p.CampSeasonId,
                p.CampSeason.Name,
                p.CampSeason.Camp.Slug,
                p.GeoJson,
                p.AreaSqm,
                p.CampSeason.SoundZone))
            .ToListAsync(cancellationToken);
    }

    public async Task<SoundZone?> GetCampSeasonSoundZoneAsync(Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.CampSeasons
            .Where(s => s.Id == campSeasonId)
            .Select(s => s.SoundZone)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<string?> GetCampSeasonNameAsync(Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.CampSeasons
            .Where(s => s.Id == campSeasonId)
            .Select(s => s.Name)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<List<CampSeasonSummaryDto>> GetCampSeasonsWithoutCampPolygonAsync(int year, CancellationToken cancellationToken = default)
    {
        return await _dbContext.CampSeasons
            .Include(s => s.Camp)
            .Where(s => s.Year == year
                && !_dbContext.CampPolygons.Any(p => p.CampSeasonId == s.Id))
            .Select(s => new CampSeasonSummaryDto(s.Id, s.Name, s.Camp.Slug))
            .ToListAsync(cancellationToken);
    }

    public async Task<List<CampPolygonHistoryEntryDto>> GetCampPolygonHistoryAsync(Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.CampPolygonHistories
            .Include(h => h.ModifiedByUser)
            .Where(h => h.CampSeasonId == campSeasonId)
            .OrderByDescending(h => h.ModifiedAt)
            .Select(h => new CampPolygonHistoryEntryDto(
                h.Id,
                h.ModifiedByUser.UserName ?? h.ModifiedByUserId.ToString(),
                h.ModifiedAt.ToString("g", null),
                h.AreaSqm,
                h.Note,
                h.GeoJson))
            .ToListAsync(cancellationToken);
    }

    public async Task<Guid?> GetUserCampSeasonIdForYearAsync(Guid userId, int year, CancellationToken cancellationToken = default)
    {
        return await _dbContext.CampLeads
            .Where(l => l.UserId == userId && l.LeftAt == null)
            .Join(_dbContext.CampSeasons,
                l => l.CampId,
                s => s.CampId,
                (l, s) => s)
            .Where(s => s.Year == year)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<(CampPolygon polygon, CampPolygonHistory history)> SaveCampPolygonAsync(
        Guid campSeasonId, string geoJson, double areaSqm, Guid modifiedByUserId,
        string note = "Saved", CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();

        var polygon = await _dbContext.CampPolygons
            .FirstOrDefaultAsync(p => p.CampSeasonId == campSeasonId, cancellationToken);

        if (polygon == null)
        {
            polygon = new CampPolygon
            {
                CampSeasonId = campSeasonId,
                GeoJson = geoJson,
                AreaSqm = areaSqm,
                LastModifiedByUserId = modifiedByUserId,
                LastModifiedAt = now
            };
            _dbContext.CampPolygons.Add(polygon);
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
            Note = note
        };
        _dbContext.CampPolygonHistories.Add(history);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return (polygon, history);
    }

    public async Task<(CampPolygon polygon, CampPolygonHistory history)> RestoreCampPolygonVersionAsync(
        Guid campSeasonId, Guid historyId, Guid restoredByUserId,
        CancellationToken cancellationToken = default)
    {
        var entry = await _dbContext.CampPolygonHistories
            .FirstOrDefaultAsync(h => h.Id == historyId && h.CampSeasonId == campSeasonId, cancellationToken)
            ?? throw new InvalidOperationException($"History entry {historyId} not found for CampSeason {campSeasonId}.");

        var localDt = entry.ModifiedAt.InUtc().LocalDateTime;
        var note = $"Restored from {localDt:yyyy-MM-dd HH:mm} UTC";
        return await SaveCampPolygonAsync(campSeasonId, entry.GeoJson, entry.AreaSqm, restoredByUserId, note, cancellationToken);
    }

    public async Task<bool> IsUserMapAdminAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        if (await _dbContext.RoleAssignments.AnyAsync(ra =>
                ra.UserId == userId &&
                (ra.RoleName == RoleNames.Admin || ra.RoleName == RoleNames.CampAdmin) &&
                ra.ValidFrom <= now &&
                (ra.ValidTo == null || ra.ValidTo > now),
                cancellationToken))
            return true;

        var team = await _dbContext.Teams
            .FirstOrDefaultAsync(t => t.Slug == _options.Value.CityPlanningTeamSlug, cancellationToken);
        if (team == null) return false;

        return await _dbContext.TeamMembers
            .AnyAsync(tm => tm.TeamId == team.Id && tm.UserId == userId && tm.LeftAt == null, cancellationToken);
    }

    public async Task<bool> CanUserEditAsync(Guid userId, Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        if (await IsUserMapAdminAsync(userId, cancellationToken)) return true;

        var settings = await GetSettingsAsync(cancellationToken);
        if (!settings.IsPlacementOpen) return false;

        var campSeason = await _dbContext.CampSeasons
            .FirstOrDefaultAsync(s => s.Id == campSeasonId, cancellationToken);
        if (campSeason == null) return false;

        return await _dbContext.CampLeads
            .AnyAsync(l => l.CampId == campSeason.CampId && l.UserId == userId && l.LeftAt == null, cancellationToken);
    }

    public async Task<CampMapSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var campSettings = await _dbContext.CampSettings
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("CampSettings row not found.");

        var settings = await _dbContext.CampMapSettings
            .FirstOrDefaultAsync(s => s.Year == campSettings.PublicYear, cancellationToken);
        if (settings != null) return settings;

        settings = new CampMapSettings
        {
            Year = campSettings.PublicYear,
            IsPlacementOpen = false,
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.CampMapSettings.Add(settings);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    public async Task OpenPlacementAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        settings.IsPlacementOpen = true;
        settings.OpenedAt = _clock.GetCurrentInstant();
        settings.UpdatedAt = _clock.GetCurrentInstant();
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ClosePlacementAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        settings.IsPlacementOpen = false;
        settings.ClosedAt = _clock.GetCurrentInstant();
        settings.UpdatedAt = _clock.GetCurrentInstant();
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateLimitZoneAsync(string geoJson, Guid userId, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        settings.LimitZoneGeoJson = geoJson;
        settings.UpdatedAt = _clock.GetCurrentInstant();
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteLimitZoneAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        settings.LimitZoneGeoJson = null;
        settings.UpdatedAt = _clock.GetCurrentInstant();
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdatePlacementDatesAsync(LocalDateTime? opensAt, LocalDateTime? closesAt, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        settings.PlacementOpensAt = opensAt;
        settings.PlacementClosesAt = closesAt;
        settings.UpdatedAt = _clock.GetCurrentInstant();
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<string> ExportAsGeoJsonAsync(int year, CancellationToken cancellationToken = default)
    {
        var polygons = await _dbContext.CampPolygons
            .Include(p => p.CampSeason).ThenInclude(s => s.Camp)
            .Where(p => p.CampSeason.Year == year)
            .ToListAsync(cancellationToken);

        var docs = new List<System.Text.Json.JsonDocument>();
        try
        {
            var features = polygons.Select(p =>
            {
                var doc = System.Text.Json.JsonDocument.Parse(p.GeoJson);
                docs.Add(doc);
                var geom = doc.RootElement.TryGetProperty("geometry", out var g) ? g : doc.RootElement;
                return new
                {
                    type = "Feature",
                    geometry = geom,
                    properties = new
                    {
                        campName = p.CampSeason.Name,
                        campSlug = p.CampSeason.Camp.Slug,
                        year = p.CampSeason.Year,
                        areaSqm = p.AreaSqm
                    }
                };
            }).ToList();

            return System.Text.Json.JsonSerializer.Serialize(
                new { type = "FeatureCollection", features },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        finally
        {
            foreach (var d in docs) d.Dispose();
        }
    }
}
