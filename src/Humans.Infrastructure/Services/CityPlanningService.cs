using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Infrastructure.Services;

public class CityPlanningService : ICityPlanningService
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;
    private readonly IOptions<CityPlanningOptions> _options;
    private readonly ICampService _campService;
    private readonly ITeamService _teamService;
    private readonly IProfileService _profileService;

    public CityPlanningService(
        HumansDbContext dbContext,
        IClock clock,
        IOptions<CityPlanningOptions> options,
        ICampService campService,
        ITeamService teamService,
        IProfileService profileService)
    {
        _dbContext = dbContext;
        _clock = clock;
        _options = options;
        _campService = campService;
        _teamService = teamService;
        _profileService = profileService;
    }

    public async Task<List<CampPolygonDto>> GetCampPolygonsAsync(int year, CancellationToken cancellationToken = default)
    {
        // Get display data for the year from camp service (keyed by campSeasonId)
        var displayData = await _campService.GetCampSeasonDisplayDataForYearAsync(year, cancellationToken);
        var seasonIds = displayData.Keys.ToList();

        // Load polygons from owned table, filtered to the year's camp season IDs
        var polygons = await _dbContext.CampPolygons
            .Where(p => seasonIds.Contains(p.CampSeasonId))
            .ToListAsync(cancellationToken);

        return polygons
            .Select(p =>
            {
                var data = displayData[p.CampSeasonId];
                return new CampPolygonDto(
                    p.CampSeasonId,
                    data.Name,
                    data.CampSlug,
                    p.GeoJson,
                    p.AreaSqm,
                    data.SoundZone);
            })
            .ToList();
    }

    public async Task<SoundZone?> GetCampSeasonSoundZoneAsync(Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        return await _campService.GetCampSeasonSoundZoneAsync(campSeasonId, cancellationToken);
    }

    public async Task<string?> GetCampSeasonNameAsync(Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        return await _campService.GetCampSeasonNameAsync(campSeasonId, cancellationToken);
    }

    public async Task<string?> GetUserDisplayNameAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Use targeted per-user lookup (GetProfileAsync) instead of GetCachedProfileAsync
        // to avoid warming the full profile cache on cold-cache paths like SignalR connect.
        var profile = await _profileService.GetProfileAsync(userId, cancellationToken);
        return profile?.BurnerName;
    }

    public async Task<List<CampSeasonSummaryDto>> GetCampSeasonsWithoutCampPolygonAsync(int year, CancellationToken cancellationToken = default)
    {
        // Get all camp seasons for the year from camp service
        var allSeasons = await _campService.GetCampSeasonBriefsForYearAsync(year, cancellationToken);
        var seasonIdsForYear = allSeasons.Select(s => s.CampSeasonId).ToList();

        // Get camp season IDs that already have polygons, filtered in SQL to this year's seasons only
        var polygonSeasonIds = await _dbContext.CampPolygons
            .Where(p => seasonIdsForYear.Contains(p.CampSeasonId))
            .Select(p => p.CampSeasonId)
            .ToListAsync(cancellationToken);

        var polygonSeasonIdSet = new HashSet<Guid>(polygonSeasonIds);

        return allSeasons
            .Where(s => !polygonSeasonIdSet.Contains(s.CampSeasonId))
            .Select(s => new CampSeasonSummaryDto(s.CampSeasonId, s.Name, s.CampSlug))
            .ToList();
    }

    public async Task<List<CampPolygonHistoryEntryDto>> GetCampPolygonHistoryAsync(Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.CampPolygonHistories
            .Include(h => h.ModifiedByUser)
            .Where(h => h.CampSeasonId == campSeasonId)
            .OrderByDescending(h => h.ModifiedAt)
            .ToListAsync(cancellationToken);

        return rows.Select(h => new CampPolygonHistoryEntryDto(
            h.Id,
            h.ModifiedByUser.DisplayName ?? h.ModifiedByUserId.ToString(),
            h.ModifiedAt.InZone(DateTimeZone.Utc).ToDateTimeUnspecified()
                .ToString("d MMM yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            h.AreaSqm,
            h.Note,
            h.GeoJson)).ToList();
    }

    public async Task<Guid?> GetUserCampSeasonIdForYearAsync(Guid userId, int year, CancellationToken cancellationToken = default)
    {
        return await _campService.GetCampLeadSeasonIdForYearAsync(userId, year, cancellationToken);
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

    public async Task<bool> IsCityPlanningTeamMemberAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var team = await _teamService.GetTeamBySlugAsync(_options.Value.CityPlanningTeamSlug, cancellationToken);
        if (team == null) return false;

        return await _teamService.IsUserMemberOfTeamAsync(team.Id, userId, cancellationToken);
    }

    public async Task<bool> CanUserEditAsync(Guid userId, Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        // Global role checks (Admin, CampAdmin) are done at the controller level via claims.
        // This method only checks team-specific access: city planning team membership + camp lead.
        if (await IsCityPlanningTeamMemberAsync(userId, cancellationToken)) return true;

        var settings = await GetSettingsAsync(cancellationToken);
        if (!settings.IsPlacementOpen) return false;

        var campSeasonInfo = await _campService.GetCampSeasonInfoAsync(campSeasonId, cancellationToken);
        if (campSeasonInfo == null) return false;
        if (campSeasonInfo.Year != settings.Year) return false;

        return await _campService.IsUserCampLeadAsync(userId, campSeasonInfo.CampId, cancellationToken);
    }

    public async Task<CityPlanningSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var campSettings = await _campService.GetSettingsAsync(cancellationToken);

        var settings = await _dbContext.CityPlanningSettings
            .FirstOrDefaultAsync(s => s.Year == campSettings.PublicYear, cancellationToken);
        if (settings != null) return settings;

        settings = new CityPlanningSettings
        {
            Year = campSettings.PublicYear,
            IsPlacementOpen = false,
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.CityPlanningSettings.Add(settings);
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

    public async Task UpdateOfficialZonesAsync(string geoJson, Guid userId, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        settings.OfficialZonesGeoJson = geoJson;
        settings.UpdatedAt = _clock.GetCurrentInstant();
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteOfficialZonesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        settings.OfficialZonesGeoJson = null;
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
        // Get display data for the year from camp service (keyed by campSeasonId)
        var displayData = await _campService.GetCampSeasonDisplayDataForYearAsync(year, cancellationToken);
        var seasonIds = displayData.Keys.ToList();

        // Load polygons from owned table, filtered to the year's camp season IDs
        var polygons = await _dbContext.CampPolygons
            .Where(p => seasonIds.Contains(p.CampSeasonId))
            .ToListAsync(cancellationToken);

        var docs = new List<System.Text.Json.JsonDocument>();
        try
        {
            var features = polygons.Select(p =>
            {
                var data = displayData[p.CampSeasonId];
                var doc = System.Text.Json.JsonDocument.Parse(p.GeoJson);
                docs.Add(doc);
                var geom = doc.RootElement.TryGetProperty("geometry", out var g) ? g : doc.RootElement;
                return new
                {
                    type = "Feature",
                    geometry = geom,
                    properties = new
                    {
                        campName = data.Name,
                        campSlug = data.CampSlug,
                        year,
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
