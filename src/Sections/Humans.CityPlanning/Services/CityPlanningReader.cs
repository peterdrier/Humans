using Humans.Base.Interfaces;
using Humans.Camps.Contracts;
using Humans.CityPlanning.Contracts;
using Humans.CityPlanning.Data;
using Humans.CityPlanning.Domain;
using Humans.Settings.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Options;
using NodaTime;
using System.Text.Json;

namespace Humans.CityPlanning.Services;

internal sealed class CityPlanningReader(
    ICityPlanningRepository repo,
    IClock clock,
    IOptions<CityPlanningOptions> options,
    ICampService campService,
    ITeamService teamService,
    IUserService userService,
    ISettingsService settingsService) : IApplicationService
{
    /// <summary>The active event's year. Null when no event is active.</summary>
    internal async Task<int?> GetCurrentYearAsync(CancellationToken cancellationToken) =>
        settingsService.GetActiveEventSettings() is { Year: > 0 } activeEvent
            ? activeEvent.Year
            : null;

    internal async Task<List<CampPolygonDto>> GetCampPolygonsAsync(
        int year, CancellationToken cancellationToken = default)
    {
        var displayData = await BuildSeasonDisplayDataAsync(year, cancellationToken);
        var seasonIds = displayData.Keys.ToList();

        var polygons = await repo.GetPolygonsByCampSeasonIdsAsync(seasonIds, cancellationToken);

        return polygons
            .Select(p =>
            {
                var data = displayData[p.CampSeasonId];
                return new CampPolygonDto(
                    p.CampSeasonId,
                    data.CampId,
                    data.Name,
                    data.CampSlug,
                    p.GeoJson,
                    p.AreaSqm,
                    data.SoundZone,
                    SpaceSizeToSqm(data.SpaceRequirement));
            })
            .ToList();
    }

    internal async Task<List<CampSeasonSummaryDto>> GetCampSeasonsWithoutCampPolygonAsync(
        int year, CancellationToken cancellationToken = default)
    {
        var displayData = await BuildSeasonDisplayDataAsync(year, cancellationToken);
        var seasonIds = displayData.Keys.ToList();

        var polygonSeasonIds = await repo.GetCampSeasonIdsWithPolygonAsync(seasonIds, cancellationToken);
        var polygonSeasonIdSet = new HashSet<Guid>(polygonSeasonIds);

        return displayData
            .Where(kvp => !polygonSeasonIdSet.Contains(kvp.Key))
            .Select(kvp => new CampSeasonSummaryDto(
                kvp.Key, kvp.Value.Name, kvp.Value.CampSlug,
                SpaceSizeToSqm(kvp.Value.SpaceRequirement), kvp.Value.SoundZone))
            .ToList();
    }

    private async Task<Dictionary<Guid, (string Name, string CampSlug, SoundZone? SoundZone, SpaceSize? SpaceRequirement, Guid CampId)>> BuildSeasonDisplayDataAsync(
        int year, CancellationToken cancellationToken)
    {
        var camps = await campService.GetCampsForYearAsync(year, cancellationToken);
        return camps
            .SelectMany(c => c.Seasons.Where(s => s.Year == year), (c, s) =>
                (SeasonId: s.Id, Data: (s.Name, c.Slug, s.SoundZone, s.SpaceRequirement, c.Id)))
            .ToDictionary(x => x.SeasonId, x => x.Data);
    }

    // Adding a SpaceSize value needs a matching case here.
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

    /// <summary>
    /// <paramref name="scheduleAnchor"/> is the active event when the row is that event's year. Any other year has no
    /// gate opening to count from, so only its override decides.
    /// </summary>
    private CityPlanningSettingsDto ToDto(CityPlanningSettings settings, EventSettingsInfo? scheduleAnchor)
    {
        var gateOpening = scheduleAnchor?.GateOpeningDate;
        var opensOn = DayFromGate(gateOpening, settings.PlacementOpensOffset);
        var closesOn = DayFromGate(gateOpening, settings.PlacementClosesOffset);
        var changeableUntilOn = DayFromGate(gateOpening, settings.PlacementChangeableUntilOffset);

        return new CityPlanningSettingsDto(
            settings.Id,
            settings.Year,
            GetPlacementState(settings.PlacementOverride, opensOn, closesOn, changeableUntilOn, scheduleAnchor),
            settings.PlacementOverride,
            settings.OpenedAt,
            settings.ClosedAt,
            settings.PlacementOpensOffset,
            settings.PlacementClosesOffset,
            settings.PlacementChangeableUntilOffset,
            opensOn,
            closesOn,
            changeableUntilOn,
            settings.RegistrationInfo,
            settings.LimitZoneGeoJson,
            settings.OfficialZonesGeoJson,
            settings.IsContainerPlacementOpen,
            settings.ContainerPlacementOpenedAt,
            settings.ContainerPlacementClosedAt,
            settings.UpdatedAt);
    }

    private static LocalDate? DayFromGate(LocalDate? gateOpening, int? offset) =>
        gateOpening is { } gate && offset is { } days ? gate.PlusDays(days) : null;

    /// <summary>
    /// A manual override wins. Otherwise, by today's date in the event's time zone: open from the opening day through
    /// the closing day (inclusive), then adjust-only through the changeable-until day, then closed. No opening day, or
    /// no event to count from, means closed.
    /// </summary>
    private BarrioPlacementState GetPlacementState(
        bool? placementOverride,
        LocalDate? opensOn,
        LocalDate? closesOn,
        LocalDate? changeableUntilOn,
        EventSettingsInfo? scheduleAnchor)
    {
        if (placementOverride is { } forced)
            return forced ? BarrioPlacementState.Open : BarrioPlacementState.Closed;

        if (scheduleAnchor is null || opensOn is not { } opens)
            return BarrioPlacementState.Closed;

        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(scheduleAnchor.TimeZoneId) ?? DateTimeZone.Utc;
        var today = clock.GetCurrentInstant().InZone(zone).Date;

        if (today < opens)
            return BarrioPlacementState.Closed;
        if (closesOn is not { } closes || today <= closes)
            return BarrioPlacementState.Open;
        if (changeableUntilOn is { } until && today <= until)
            return BarrioPlacementState.Changeable;
        return BarrioPlacementState.Closed;
    }

    internal async Task<List<CampPolygonHistoryEntryDto>> GetCampPolygonHistoryAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        var rows = await repo.GetHistoryForCampSeasonAsync(campSeasonId, cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        var userIds = rows.Select(r => r.ModifiedByUserId).Distinct().ToList();
        var users = await userService.GetUserInfosAsync(userIds, cancellationToken);

        return rows.Select(h =>
        {
            var displayName = users.TryGetValue(h.ModifiedByUserId, out var user) && !string.IsNullOrEmpty(user.BurnerName)
                ? user.BurnerName
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

    internal async Task<bool> IsCityPlanningTeamMemberAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        // Both sides lower-cased, so case never decides. No configured slug means no exemption, not a blanket one.
        var configuredSlug = options.Value.CityPlanningTeamSlug;
        if (string.IsNullOrWhiteSpace(configuredSlug)) return false;

        var normalizedSlug = configuredSlug.ToLowerInvariant();
        var team = (await teamService.GetTeamsAsync(cancellationToken)).Values
            .FirstOrDefault(t =>
                string.Equals(t.Slug.ToLowerInvariant(), normalizedSlug, StringComparison.Ordinal) ||
                string.Equals(t.CustomSlug?.ToLowerInvariant(), normalizedSlug, StringComparison.Ordinal));
        return team is { IsActive: true } && team.Members.Any(m => m.UserId == userId);
    }

    internal async Task<bool> CanUserEditAsync(
        Guid userId, Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        // Team-specific access only; Admin/CampAdmin claims are checked at the controller.
        if (await IsCityPlanningTeamMemberAsync(userId, cancellationToken)) return true;

        var settings = await GetSettingsAsync(cancellationToken);
        if (settings is null || settings.PlacementState == BarrioPlacementState.Closed) return false;

        var season = await campService.GetCampSeasonByIdAsync(campSeasonId, cancellationToken);
        if (season is null) return false;
        if (season.Year != settings.Year) return false;

        if (!season.IsLead(userId)) return false;

        // After the open window a lead may still adjust the polygon they placed, never create one.
        return settings.PlacementState == BarrioPlacementState.Open
            || (await repo.GetCampSeasonIdsWithPolygonAsync([campSeasonId], cancellationToken)).Count > 0;
    }

    internal async Task<CityPlanningSettingsDto?> GetSettingsAsync(
        CancellationToken cancellationToken = default, int? year = null)
    {
        var activeEvent = settingsService.GetActiveEventSettings() is { Year: > 0 } active
            ? active
            : null;
        if ((year ?? activeEvent?.Year) is not { } settingsYear)
            return null;

        // Never writes: an unconfigured year reads as unpersisted defaults (Id = Guid.Empty).
        var settings = await repo.GetSettingsAsync(settingsYear, cancellationToken)
            ?? new CityPlanningSettings { Id = Guid.Empty, Year = settingsYear };
        return ToDto(settings, activeEvent?.Year == settingsYear ? activeEvent : null);
    }

    internal async Task<string?> GetRegistrationInfoAsync(CancellationToken cancellationToken = default)
    {
        if (await GetRegistrationTargetYearAsync(cancellationToken) is not { } targetYear)
            return null;

        return (await repo.GetSettingsAsync(targetYear, cancellationToken))?.RegistrationInfo;
    }

    // The Register page targets the highest open season, else the active event's year.
    internal async Task<int?> GetRegistrationTargetYearAsync(CancellationToken ct)
    {
        var campSettings = await campService.GetSettingsAsync(ct);
        return campSettings.OpenSeasons.Count > 0
            ? campSettings.OpenSeasons.Max()
            : await GetCurrentYearAsync(ct);
    }

    internal async Task<string> ExportAsGeoJsonAsync(int year, CancellationToken cancellationToken = default)
    {
        var displayData = await BuildSeasonDisplayDataAsync(year, cancellationToken);
        var seasonIds = displayData.Keys.ToList();

        var polygons = await repo.GetPolygonsByCampSeasonIdsAsync(seasonIds, cancellationToken);

        var features = polygons.Select(p =>
        {
            var data = displayData[p.CampSeasonId];
            using var doc = JsonDocument.Parse(p.GeoJson);
            var geom = doc.RootElement.TryGetProperty("geometry", out var g) ? g : doc.RootElement;
            return new
            {
                type = "Feature",
                geometry = geom.Clone(),
                properties = new
                {
                    campName = data.Name,
                    campSlug = data.CampSlug,
                    year,
                    areaSqm = p.AreaSqm
                }
            };
        }).ToList();

        return JsonSerializer.Serialize(
            new { type = "FeatureCollection", features },
            new JsonSerializerOptions { WriteIndented = true });
    }
}
