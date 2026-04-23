using Humans.Application.Configuration;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.CitiPlanning;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Application.Services.CityPlanning;

/// <summary>
/// Application-layer implementation of <see cref="ICityPlanningService"/>.
/// Goes through <see cref="ICityPlanningRepository"/> for all data access —
/// this type never imports <c>Microsoft.EntityFrameworkCore</c>, enforced by
/// <c>Humans.Application.csproj</c>'s reference graph.
/// </summary>
/// <remarks>
/// Cross-section reads route through the other sections' public service
/// interfaces: <see cref="ICampService"/> for camp season metadata,
/// <see cref="ITeamService"/> for city-planning team membership,
/// <see cref="IProfileService"/> for burner names on display, and
/// <see cref="IUserService"/> for user display names on polygon history
/// (replaces the prior cross-domain <c>.Include(h => h.ModifiedByUser)</c>).
/// </remarks>
public sealed class CityPlanningService : ICityPlanningService
{
    private readonly ICityPlanningRepository _repo;
    private readonly IClock _clock;
    private readonly IOptions<CityPlanningOptions> _options;
    private readonly ICampService _campService;
    private readonly ITeamService _teamService;
    private readonly IProfileService _profileService;
    private readonly IUserService _userService;

    public CityPlanningService(
        ICityPlanningRepository repo,
        IClock clock,
        IOptions<CityPlanningOptions> options,
        ICampService campService,
        ITeamService teamService,
        IProfileService profileService,
        IUserService userService)
    {
        _repo = repo;
        _clock = clock;
        _options = options;
        _campService = campService;
        _teamService = teamService;
        _profileService = profileService;
        _userService = userService;
    }

    // ==========================================================================
    // Polygon reads
    // ==========================================================================

    public async Task<List<CampPolygonDto>> GetCampPolygonsAsync(
        int year, CancellationToken cancellationToken = default)
    {
        // Get display data for the year from camp service (keyed by campSeasonId).
        var displayData = await _campService.GetCampSeasonDisplayDataForYearAsync(year, cancellationToken);
        var seasonIds = displayData.Keys.ToList();

        var polygons = await _repo.GetPolygonsByCampSeasonIdsAsync(seasonIds, cancellationToken);

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
                    data.SoundZone,
                    SpaceSizeToSqm(data.SpaceRequirement));
            })
            .ToList();
    }

    public async Task<string?> GetUserDisplayNameAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        // Use targeted per-user lookup (GetProfileAsync) instead of GetFullProfileAsync
        // to avoid warming the full profile cache on cold-cache paths like SignalR connect.
        var profile = await _profileService.GetProfileAsync(userId, cancellationToken);
        return profile?.BurnerName;
    }

    public async Task<List<CampSeasonSummaryDto>> GetCampSeasonsWithoutCampPolygonAsync(
        int year, CancellationToken cancellationToken = default)
    {
        // Get display data for the year from camp service (keyed by campSeasonId).
        var displayData = await _campService.GetCampSeasonDisplayDataForYearAsync(year, cancellationToken);
        var seasonIds = displayData.Keys.ToList();

        var polygonSeasonIds = await _repo.GetCampSeasonIdsWithPolygonAsync(seasonIds, cancellationToken);
        var polygonSeasonIdSet = new HashSet<Guid>(polygonSeasonIds);

        return displayData
            .Where(kvp => !polygonSeasonIdSet.Contains(kvp.Key))
            .Select(kvp => new CampSeasonSummaryDto(
                kvp.Key, kvp.Value.Name, kvp.Value.CampSlug,
                SpaceSizeToSqm(kvp.Value.SpaceRequirement), kvp.Value.SoundZone))
            .ToList();
    }

    // Keep in sync with SpaceSize enum — adding a new enum value requires a matching case here.
    private static double? SpaceSizeToSqm(SpaceSize? size) => size switch
    {
        SpaceSize.Sqm150 => 150,
        SpaceSize.Sqm300 => 300,
        SpaceSize.Sqm450 => 450,
        SpaceSize.Sqm600 => 600,
        SpaceSize.Sqm800 => 800,
        SpaceSize.Sqm1000 => 1000,
        SpaceSize.Sqm1200 => 1200,
        SpaceSize.Sqm1500 => 1500,
        SpaceSize.Sqm1800 => 1800,
        SpaceSize.Sqm2200 => 2200,
        SpaceSize.Sqm2800 => 2800,
        _ => null
    };

    public async Task<List<CampPolygonHistoryEntryDto>> GetCampPolygonHistoryAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        var rows = await _repo.GetHistoryForCampSeasonAsync(campSeasonId, cancellationToken);
        if (rows.Count == 0)
            return [];

        // Resolve display names through IUserService — no cross-domain .Include().
        var userIds = rows.Select(r => r.ModifiedByUserId).Distinct().ToList();
        var users = await _userService.GetByIdsAsync(userIds, cancellationToken);

        return rows.Select(h =>
        {
            var displayName = users.TryGetValue(h.ModifiedByUserId, out var user) && !string.IsNullOrEmpty(user.DisplayName)
                ? user.DisplayName
                : h.ModifiedByUserId.ToString();

            return new CampPolygonHistoryEntryDto(
                h.Id,
                displayName,
                h.ModifiedAt,
                h.AreaSqm,
                h.Note,
                h.GeoJson);
        }).ToList();
    }

    // ==========================================================================
    // Polygon writes
    // ==========================================================================

    public Task<(CampPolygon polygon, CampPolygonHistory history)> SaveCampPolygonAsync(
        Guid campSeasonId, string geoJson, double areaSqm, Guid modifiedByUserId,
        string note = "Saved", CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        return _repo.SavePolygonAndAppendHistoryAsync(
            campSeasonId, geoJson, areaSqm, modifiedByUserId, note, now, cancellationToken);
    }

    public async Task<(CampPolygon polygon, CampPolygonHistory history)> RestoreCampPolygonVersionAsync(
        Guid campSeasonId, Guid historyId, Guid restoredByUserId,
        CancellationToken cancellationToken = default)
    {
        var entry = await _repo.GetHistoryEntryAsync(campSeasonId, historyId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"History entry {historyId} not found for CampSeason {campSeasonId}.");

        var localDt = entry.ModifiedAt.InUtc().LocalDateTime;
        var note = $"Restored from {localDt:yyyy-MM-dd HH:mm} UTC";
        return await SaveCampPolygonAsync(
            campSeasonId, entry.GeoJson, entry.AreaSqm, restoredByUserId, note, cancellationToken);
    }

    // ==========================================================================
    // Authorization
    // ==========================================================================

    public async Task<bool> IsCityPlanningTeamMemberAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var team = await _teamService.GetTeamBySlugAsync(_options.Value.CityPlanningTeamSlug, cancellationToken);
        if (team is null) return false;

        return await _teamService.IsUserMemberOfTeamAsync(team.Id, userId, cancellationToken);
    }

    public async Task<bool> CanUserEditAsync(
        Guid userId, Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        // Global role checks (Admin, CampAdmin) are done at the controller level via claims.
        // This method only checks team-specific access: city planning team membership + camp lead.
        if (await IsCityPlanningTeamMemberAsync(userId, cancellationToken)) return true;

        var settings = await GetSettingsAsync(cancellationToken);
        if (!settings.IsPlacementOpen) return false;

        var campSeasonInfo = await _campService.GetCampSeasonInfoAsync(campSeasonId, cancellationToken);
        if (campSeasonInfo is null) return false;
        if (campSeasonInfo.Year != settings.Year) return false;

        return await _campService.IsUserCampLeadAsync(userId, campSeasonInfo.CampId, cancellationToken);
    }

    // ==========================================================================
    // Settings
    // ==========================================================================

    public async Task<CityPlanningSettings> GetSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var campSettings = await _campService.GetSettingsAsync(cancellationToken);
        return await _repo.GetOrCreateSettingsAsync(
            campSettings.PublicYear, _clock.GetCurrentInstant(), cancellationToken);
    }

    public async Task OpenPlacementAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var campSettings = await _campService.GetSettingsAsync(cancellationToken);
        var now = _clock.GetCurrentInstant();
        await _repo.MutateSettingsAsync(
            campSettings.PublicYear,
            s =>
            {
                s.IsPlacementOpen = true;
                s.OpenedAt = now;
            },
            now,
            cancellationToken);
    }

    public async Task ClosePlacementAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var campSettings = await _campService.GetSettingsAsync(cancellationToken);
        var now = _clock.GetCurrentInstant();
        await _repo.MutateSettingsAsync(
            campSettings.PublicYear,
            s =>
            {
                s.IsPlacementOpen = false;
                s.ClosedAt = now;
            },
            now,
            cancellationToken);
    }

    public async Task UpdateLimitZoneAsync(
        string geoJson, Guid userId, CancellationToken cancellationToken = default)
    {
        var campSettings = await _campService.GetSettingsAsync(cancellationToken);
        await _repo.MutateSettingsAsync(
            campSettings.PublicYear,
            s => s.LimitZoneGeoJson = geoJson,
            _clock.GetCurrentInstant(),
            cancellationToken);
    }

    public async Task DeleteLimitZoneAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var campSettings = await _campService.GetSettingsAsync(cancellationToken);
        await _repo.MutateSettingsAsync(
            campSettings.PublicYear,
            s => s.LimitZoneGeoJson = null,
            _clock.GetCurrentInstant(),
            cancellationToken);
    }

    public async Task UpdateOfficialZonesAsync(
        string geoJson, Guid userId, CancellationToken cancellationToken = default)
    {
        var campSettings = await _campService.GetSettingsAsync(cancellationToken);
        await _repo.MutateSettingsAsync(
            campSettings.PublicYear,
            s => s.OfficialZonesGeoJson = geoJson,
            _clock.GetCurrentInstant(),
            cancellationToken);
    }

    public async Task DeleteOfficialZonesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var campSettings = await _campService.GetSettingsAsync(cancellationToken);
        await _repo.MutateSettingsAsync(
            campSettings.PublicYear,
            s => s.OfficialZonesGeoJson = null,
            _clock.GetCurrentInstant(),
            cancellationToken);
    }

    public async Task UpdatePlacementDatesAsync(
        LocalDateTime? opensAt, LocalDateTime? closesAt, CancellationToken cancellationToken = default)
    {
        var campSettings = await _campService.GetSettingsAsync(cancellationToken);
        await _repo.MutateSettingsAsync(
            campSettings.PublicYear,
            s =>
            {
                s.PlacementOpensAt = opensAt;
                s.PlacementClosesAt = closesAt;
            },
            _clock.GetCurrentInstant(),
            cancellationToken);
    }

    public async Task<string?> GetRegistrationInfoAsync(CancellationToken cancellationToken = default)
    {
        var targetYear = await GetRegistrationTargetYearAsync(cancellationToken);
        var settings = await _repo.GetOrCreateSettingsAsync(
            targetYear, _clock.GetCurrentInstant(), cancellationToken);
        return settings.RegistrationInfo;
    }

    public async Task UpdateRegistrationInfoAsync(
        string? registrationInfo, CancellationToken cancellationToken = default)
    {
        var targetYear = await GetRegistrationTargetYearAsync(cancellationToken);
        var trimmed = string.IsNullOrWhiteSpace(registrationInfo) ? null : registrationInfo.Trim();
        await _repo.MutateSettingsAsync(
            targetYear,
            s => s.RegistrationInfo = trimmed,
            _clock.GetCurrentInstant(),
            cancellationToken);
    }

    // Registration info is shown on /Barrios/Register, which targets the highest open
    // season year (see CampController.PopulateRegisterSeasonYearAsync). PublicYear can
    // lag behind the newest open season during transitions, so key this row to the same
    // year the register page uses to avoid editing/showing the wrong year's content.
    private async Task<int> GetRegistrationTargetYearAsync(CancellationToken ct)
    {
        var campSettings = await _campService.GetSettingsAsync(ct);
        return campSettings.OpenSeasons.Count > 0
            ? campSettings.OpenSeasons.Max()
            : campSettings.PublicYear;
    }

    // ==========================================================================
    // Export
    // ==========================================================================

    public async Task<string> ExportAsGeoJsonAsync(int year, CancellationToken cancellationToken = default)
    {
        // Get display data for the year from camp service (keyed by campSeasonId).
        var displayData = await _campService.GetCampSeasonDisplayDataForYearAsync(year, cancellationToken);
        var seasonIds = displayData.Keys.ToList();

        var polygons = await _repo.GetPolygonsByCampSeasonIdsAsync(seasonIds, cancellationToken);

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
