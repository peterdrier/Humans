using Humans.CityPlanning.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Camps.Contracts;

using Humans.CityPlanning.Data;
using Humans.Teams.Contracts;
using Humans.CityPlanning.Domain;
using Humans.Settings.Contracts;
using Microsoft.Extensions.Options;
using NodaTime;
using System.Globalization;
using System.Text.Json;
using Humans.Users.Contracts;

namespace Humans.CityPlanning.Services;

/// <summary>
/// The City Planning section service. Implements the cross-section surface
/// (<see cref="ICityPlanningService"/> and <see cref="ICityPlanningWrite"/> on the contracts leaf) and, beyond it, the polygon,
/// placement-phase, zone and export operations the section's own controllers call directly.
/// Cross-section reads via ICampService/ITeamService/IUserService; the current year is the
/// active event's (ISettingsService); settings writes record their actor through the Audit crosscut.
/// </summary>
internal sealed class CityPlanningService(
    ICityPlanningRepository repo,
    IClock clock,
    IOptions<CityPlanningOptions> options,
    ICampService campService,
    ITeamService teamService,
    IUserService userService,
    IAuditLogService auditLog,
    ISettingsService settingsService) : ICityPlanningService, ICityPlanningWrite
{
    private const long MaxGeoJsonUploadBytes = 10 * 1024 * 1024;

    /// <summary>The current city-planning year: the active event's. Null when no event is active.</summary>
    private async Task<int?> GetCurrentYearAsync(CancellationToken cancellationToken) =>
        await settingsService.GetActiveEventSettingsAsync(cancellationToken) is { Year: > 0 } activeEvent
            ? activeEvent.Year
            : null;

    private async Task<int> RequireCurrentYearAsync(CancellationToken cancellationToken) =>
        await GetCurrentYearAsync(cancellationToken)
        ?? throw new InvalidOperationException("No active event: city planning has no current year to write to.");

    /// <summary>
    /// Applies a settings change for the active event's year and records who made it. Every caller is
    /// an organiser acting on the members' behalf, and <c>CityPlanningSettings</c> stores only
    /// when a value changed — the actor exists nowhere but the audit log.
    ///
    /// <para><c>LogAsync</c> deliberately takes no cancellation token, so nothing cancellable may
    /// sit between the committing write and the audit call. Two things follow. The row id is
    /// resolved <em>before</em> the write, so no post-save re-read stands in the way. And the write
    /// itself runs on <see cref="CancellationToken.None"/>: the repository passes its token into
    /// <c>SaveChangesAsync</c>, and a request aborted while that command is in flight can leave the
    /// row committed on the server while the await reports cancellation — the change lands and its
    /// actor record does not. Everything up to that point stays cancellable, because abandoning it
    /// writes no value change worth an entry.</para>
    ///
    /// <para>Both repository calls create the row on demand — the first admin write is what
    /// persists a year's row; reads never do — and nothing deletes it, so the id read here is the
    /// id the mutation writes.</para>
    /// </summary>
    private async Task MutateSettingsAndAuditAsync(
        Action<CityPlanningSettings, Instant> mutate,
        AuditAction action,
        string description,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var year = await RequireCurrentYearAsync(cancellationToken);
        var now = clock.GetCurrentInstant();

        var settings = await repo.GetOrCreateSettingsAsync(year, now, cancellationToken);

        await repo.MutateSettingsAsync(
            year, s => mutate(s, now), now, CancellationToken.None);

        await auditLog.LogAsync(
            action, nameof(CityPlanningSettings), settings.Id, description, userId);
    }

    // --- Polygon reads ---

    public async Task<List<CampPolygonDto>> GetCampPolygonsAsync(
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

    public async Task<List<CampSeasonSummaryDto>> GetCampSeasonsWithoutCampPolygonAsync(
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

    /// <summary>
    /// Builds the season-id → display-data map via LINQ over the cached
    /// <see cref="ICampService.GetCampsForYearAsync"/> projection.
    /// </summary>
    private async Task<Dictionary<Guid, (string Name, string CampSlug, SoundZone? SoundZone, SpaceSize? SpaceRequirement, Guid CampId)>> BuildSeasonDisplayDataAsync(
        int year, CancellationToken cancellationToken)
    {
        var camps = await campService.GetCampsForYearAsync(year, cancellationToken);
        return camps
            .SelectMany(c => c.Seasons.Where(s => s.Year == year), (c, s) =>
                (SeasonId: s.Id, Data: (s.Name, c.Slug, s.SoundZone, s.SpaceRequirement, c.Id)))
            .ToDictionary(x => x.SeasonId, x => x.Data);
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

    /// <summary>
    /// Projects a settings row, resolving its barrio placement window against
    /// <paramref name="scheduleAnchor"/> — the active event, when the row is that event's year.
    /// Any other year has no gate opening to count from, so only its override decides.
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
    /// The barrio placement window in effect now. A manual override wins outright. Otherwise the
    /// schedule decides, by today's date in the event's time zone: open from the opening day through
    /// the closing day (inclusive), then adjust-only through the changeable-until day, then closed.
    /// No opening day — or no event to count from — means closed.
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

    public async Task<List<CampPolygonHistoryEntryDto>> GetCampPolygonHistoryAsync(
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

    // --- Polygon writes ---

    public async Task<CampPolygonSaveResult> SaveCampPolygonAsync(
        Guid campSeasonId, string geoJson, double areaSqm, Guid modifiedByUserId,
        string note = "Saved", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(geoJson) || !IsValidCampGeometry(geoJson))
            throw new ArgumentException("Invalid GeoJSON.", nameof(geoJson));
        if (!double.IsFinite(areaSqm) || areaSqm < 0)
            throw new ArgumentException("Area must be finite and non-negative.", nameof(areaSqm));

        var now = clock.GetCurrentInstant();
        var polygon = await repo.SavePolygonAndAppendHistoryAsync(
            campSeasonId, geoJson, areaSqm, modifiedByUserId, note, now, cancellationToken);
        return new CampPolygonSaveResult(polygon.GeoJson, polygon.AreaSqm);
    }

    private static bool IsValidCampGeometry(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            var geometry = document.RootElement;
            if (geometry.ValueKind != JsonValueKind.Object) return false;
            if (HasType(geometry, "Feature"))
            {
                if (!geometry.TryGetProperty("geometry", out geometry) ||
                    geometry.ValueKind != JsonValueKind.Object) return false;
            }
            if (!geometry.TryGetProperty("coordinates", out var coordinates) ||
                coordinates.ValueKind != JsonValueKind.Array || coordinates.GetArrayLength() == 0) return false;
            if (HasType(geometry, "Polygon")) return IsPolygon(coordinates);
            if (!HasType(geometry, "MultiPolygon")) return false;
            return coordinates.EnumerateArray().All(IsPolygon);
        }
        catch (JsonException)
        {
            return false;
        }

        static bool HasType(JsonElement element, string type) =>
            element.TryGetProperty("type", out var value) &&
            value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), type, StringComparison.Ordinal);

        static bool IsPolygon(JsonElement rings)
        {
            if (rings.ValueKind != JsonValueKind.Array || rings.GetArrayLength() == 0) return false;
            foreach (var ring in rings.EnumerateArray())
            {
                if (ring.ValueKind != JsonValueKind.Array || ring.GetArrayLength() < 4) return false;
                foreach (var position in ring.EnumerateArray())
                {
                    if (position.ValueKind != JsonValueKind.Array || position.GetArrayLength() < 2) return false;
                    foreach (var coordinate in position.EnumerateArray())
                        if (coordinate.ValueKind != JsonValueKind.Number ||
                            !coordinate.TryGetDouble(out var number) || !double.IsFinite(number)) return false;
                    if (position[0].GetDouble() is < -180 or > 180 || position[1].GetDouble() is < -90 or > 90) return false;
                }
                var first = ring[0];
                var last = ring[ring.GetArrayLength() - 1];
                if (first.GetArrayLength() != last.GetArrayLength()) return false;
                for (var i = 0; i < first.GetArrayLength(); i++)
                    if (first[i].GetDouble() != last[i].GetDouble()) return false;
            }
            return true;
        }
    }

    private static bool IsValidJson(string value)
    {
        try
        {
            JsonDocument.Parse(value).Dispose();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Null when the history entry is not one of this camp season's.</summary>
    public async Task<CampPolygonSaveResult?> RestoreCampPolygonVersionAsync(
        Guid campSeasonId, Guid historyId, Guid restoredByUserId,
        CancellationToken cancellationToken = default)
    {
        var entry = await repo.GetHistoryEntryAsync(campSeasonId, historyId, cancellationToken);
        if (entry is null) return null;

        var note = $"Restored from {entry.ModifiedAt.ToDateTimeUtc().ToInvariantTimestamp()} UTC";
        return await SaveCampPolygonAsync(
            campSeasonId, entry.GeoJson, entry.AreaSqm, restoredByUserId, note, cancellationToken);
    }

    public Task<int> DeleteCampPolygonsForSeasonsAsync(
        IReadOnlyCollection<Guid> campSeasonIds, CancellationToken cancellationToken = default) =>
        repo.DeletePolygonsForCampSeasonsAsync(campSeasonIds, cancellationToken);

    // --- Authorization ---

    public async Task<bool> IsCityPlanningTeamMemberAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        // Both sides are lower-cased before comparing, so a stored slug's case doesn't
        // affect matching. The option defaults to string.Empty; no configured slug means
        // no exemption, not a blanket one.
        var configuredSlug = options.Value.CityPlanningTeamSlug;
        if (string.IsNullOrWhiteSpace(configuredSlug)) return false;

        var normalizedSlug = configuredSlug.ToLowerInvariant();
        var team = (await teamService.GetTeamsAsync(cancellationToken)).Values
            .FirstOrDefault(t =>
                string.Equals(t.Slug.ToLowerInvariant(), normalizedSlug, StringComparison.Ordinal) ||
                string.Equals(t.CustomSlug?.ToLowerInvariant(), normalizedSlug, StringComparison.Ordinal));
        return team is { IsActive: true } && team.Members.Any(m => m.UserId == userId);
    }

    public async Task<bool> CanUserEditAsync(
        Guid userId, Guid campSeasonId, CancellationToken cancellationToken = default)
    {
        // Team-specific access only; Admin/CampAdmin claims are checked at the controller.
        if (await IsCityPlanningTeamMemberAsync(userId, cancellationToken)) return true;

        var settings = await GetSettingsAsync(cancellationToken);
        if (settings is null || settings.PlacementState == BarrioPlacementState.Closed) return false;

        var season = await campService.GetCampSeasonByIdAsync(campSeasonId, cancellationToken);
        if (season is null) return false;
        if (season.Year != settings.Year) return false;

        var camp = (await campService.GetCampsForYearAsync(settings.Year, cancellationToken))
            .FirstOrDefault(c => c.Id == season.CampId);
        if (camp?.IsLead(userId) != true) return false;

        // After the open window a lead may still adjust the polygon they placed, never create one.
        return settings.PlacementState == BarrioPlacementState.Open
            || (await repo.GetCampSeasonIdsWithPolygonAsync([campSeasonId], cancellationToken)).Count > 0;
    }

    // --- Settings ---

    public async Task<CityPlanningSettingsDto?> GetSettingsAsync(
        CancellationToken cancellationToken = default, int? year = null)
    {
        var activeEvent = await settingsService.GetActiveEventSettingsAsync(cancellationToken) is { Year: > 0 } active
            ? active
            : null;
        if ((year ?? activeEvent?.Year) is not { } settingsYear)
            return null;

        // Side-effect free: a year nobody has configured yet reads as the defaults, unpersisted
        // (Id = Guid.Empty). The first admin write creates the row.
        var settings = await repo.GetSettingsAsync(settingsYear, cancellationToken)
            ?? new CityPlanningSettings { Id = Guid.Empty, Year = settingsYear };
        return ToDto(settings, activeEvent?.Year == settingsYear ? activeEvent : null);
    }

    public Task OpenPlacementAsync(Guid userId, CancellationToken cancellationToken = default) =>
        MutateSettingsAndAuditAsync(
            (s, now) =>
            {
                s.PlacementOverride = true;
                s.OpenedAt = now;
            },
            AuditAction.CityPlanningPlacementOpened,
            "Forced barrio placement open (manual override of the schedule)",
            userId,
            cancellationToken);

    public Task ClosePlacementAsync(Guid userId, CancellationToken cancellationToken = default) =>
        MutateSettingsAndAuditAsync(
            (s, now) =>
            {
                s.PlacementOverride = false;
                s.ClosedAt = now;
            },
            AuditAction.CityPlanningPlacementClosed,
            "Forced barrio placement closed (manual override of the schedule)",
            userId,
            cancellationToken);

    /// <summary>Drops the manual override so barrio placement follows the day-offset schedule again.</summary>
    public Task ClearPlacementOverrideAsync(Guid userId, CancellationToken cancellationToken = default) =>
        MutateSettingsAndAuditAsync(
            (s, _) => s.PlacementOverride = null,
            AuditAction.CityPlanningPlacementOverrideCleared,
            "Cleared the barrio placement override — placement follows the schedule",
            userId,
            cancellationToken);

    public Task OpenContainerPlacementAsync(Guid userId, CancellationToken cancellationToken = default) =>
        MutateSettingsAndAuditAsync(
            (s, now) =>
            {
                s.IsContainerPlacementOpen = true;
                s.ContainerPlacementOpenedAt = now;
            },
            AuditAction.CityPlanningContainerPlacementOpened,
            "Opened container placement",
            userId,
            cancellationToken);

    public Task CloseContainerPlacementAsync(Guid userId, CancellationToken cancellationToken = default) =>
        MutateSettingsAndAuditAsync(
            (s, now) =>
            {
                s.IsContainerPlacementOpen = false;
                s.ContainerPlacementClosedAt = now;
            },
            AuditAction.CityPlanningContainerPlacementClosed,
            "Closed container placement",
            userId,
            cancellationToken);

    public Task UpdateLimitZoneAsync(
        string geoJson, Guid userId, CancellationToken cancellationToken = default) =>
        MutateSettingsAndAuditAsync(
            (s, _) => s.LimitZoneGeoJson = geoJson,
            AuditAction.CityPlanningLimitZoneUpdated,
            "Uploaded the site boundary",
            userId,
            cancellationToken);

    public async Task<GeoJsonUploadResult> UpdateLimitZoneFromUploadAsync(
        IFormFile? file, Guid userId, CancellationToken cancellationToken = default)
    {
        var geoJson = await ReadGeoJsonUploadAsync(file, cancellationToken);
        if (!geoJson.Success)
            return new GeoJsonUploadResult(false, geoJson.ErrorKey);

        await UpdateLimitZoneAsync(geoJson.Content!, userId, cancellationToken);
        return new GeoJsonUploadResult(true);
    }

    private static async Task<(bool Success, string? ErrorKey, string? Content)> ReadGeoJsonUploadAsync(
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return (false, "MissingFile", null);

        if (file.Length > MaxGeoJsonUploadBytes)
            return (false, "FileTooLarge", null);

        using var reader = new StreamReader(file.OpenReadStream());
        var geoJson = await reader.ReadToEndAsync(cancellationToken);
        return IsValidJson(geoJson)
            ? (true, null, geoJson)
            : (false, "InvalidGeoJson", null);
    }

    public Task DeleteLimitZoneAsync(Guid userId, CancellationToken cancellationToken = default) =>
        MutateSettingsAndAuditAsync(
            (s, _) => s.LimitZoneGeoJson = null,
            AuditAction.CityPlanningLimitZoneDeleted,
            "Deleted the site boundary",
            userId,
            cancellationToken);

    public Task UpdateOfficialZonesAsync(
        string geoJson, Guid userId, CancellationToken cancellationToken = default) =>
        MutateSettingsAndAuditAsync(
            (s, _) => s.OfficialZonesGeoJson = geoJson,
            AuditAction.CityPlanningOfficialZonesUpdated,
            "Uploaded the official zones",
            userId,
            cancellationToken);

    public async Task<GeoJsonUploadResult> UpdateOfficialZonesFromUploadAsync(
        IFormFile? file, Guid userId, CancellationToken cancellationToken = default)
    {
        var geoJson = await ReadGeoJsonUploadAsync(file, cancellationToken);
        if (!geoJson.Success)
            return new GeoJsonUploadResult(false, geoJson.ErrorKey);

        await UpdateOfficialZonesAsync(geoJson.Content!, userId, cancellationToken);
        return new GeoJsonUploadResult(true);
    }

    public Task DeleteOfficialZonesAsync(Guid userId, CancellationToken cancellationToken = default) =>
        MutateSettingsAndAuditAsync(
            (s, _) => s.OfficialZonesGeoJson = null,
            AuditAction.CityPlanningOfficialZonesDeleted,
            "Deleted the official zones",
            userId,
            cancellationToken);

    /// <summary>Furthest a schedule day may sit from gate opening, either side.</summary>
    internal const int MaxPlacementOffsetDays = 366;

    /// <summary>
    /// Sets the barrio placement schedule as day offsets from the active event's gate opening
    /// (negative = before). Null clears that marker. The days must run open ≤ close ≤ changeable-until,
    /// and a changeable-until day needs a closing day to follow.
    /// </summary>
    public async Task<PlacementScheduleUpdateResult> UpdatePlacementScheduleAsync(
        int? opensOffset, int? closesOffset, int? changeableUntilOffset,
        Guid userId, CancellationToken cancellationToken = default)
    {
        if (new[] { opensOffset, closesOffset, changeableUntilOffset }
            .Any(o => o is { } days && (days < -MaxPlacementOffsetDays || days > MaxPlacementOffsetDays)))
            return new PlacementScheduleUpdateResult(false, "OutOfRange");
        if (opensOffset is { } opens && closesOffset is { } closes && closes < opens)
            return new PlacementScheduleUpdateResult(false, "ClosesBeforeOpens");
        if (changeableUntilOffset is { } until)
        {
            if (closesOffset is not { } closesDay)
                return new PlacementScheduleUpdateResult(false, "ChangeableWithoutClose");
            if (until < closesDay)
                return new PlacementScheduleUpdateResult(false, "ChangeableBeforeCloses");
        }

        await MutateSettingsAndAuditAsync(
            (s, _) =>
            {
                s.PlacementOpensOffset = opensOffset;
                s.PlacementClosesOffset = closesOffset;
                s.PlacementChangeableUntilOffset = changeableUntilOffset;
            },
            AuditAction.CityPlanningPlacementDatesUpdated,
            $"Set the barrio placement schedule (days from gate opening): opens {FormatOffset(opensOffset)}, " +
            $"closes {FormatOffset(closesOffset)}, changeable until {FormatOffset(changeableUntilOffset)}",
            userId,
            cancellationToken);
        return new PlacementScheduleUpdateResult(true);
    }

    private static string FormatOffset(int? offset) =>
        offset is { } days ? days.ToString("+0;-0;0", CultureInfo.InvariantCulture) : "unset";

    public async Task<string?> GetRegistrationInfoAsync(CancellationToken cancellationToken = default)
    {
        if (await GetRegistrationTargetYearAsync(cancellationToken) is not { } targetYear)
            return null;

        return (await repo.GetSettingsAsync(targetYear, cancellationToken))?.RegistrationInfo;
    }

    public async Task UpdateRegistrationInfoAsync(
        string? registrationInfo, Guid userId, CancellationToken cancellationToken = default)
    {
        var targetYear = await GetRegistrationTargetYearAsync(cancellationToken)
            ?? throw new InvalidOperationException("No open season and no active event: registration info has no year to write to.");
        var trimmed = string.IsNullOrWhiteSpace(registrationInfo) ? null : registrationInfo.Trim();
        var now = clock.GetCurrentInstant();
        // Same ordering as MutateSettingsAndAuditAsync, but keyed to the registration target year.
        var settings = await repo.GetOrCreateSettingsAsync(targetYear, now, cancellationToken);
        await repo.MutateSettingsAsync(
            targetYear, s => s.RegistrationInfo = trimmed, now, CancellationToken.None);
        await auditLog.LogAsync(
            AuditAction.CityPlanningRegistrationInfoUpdated, nameof(CityPlanningSettings), settings.Id,
            trimmed is null ? $"Cleared the {targetYear} registration info" : $"Updated the {targetYear} registration info",
            userId);
    }

    // Key registration-info to the highest open season (Register page targets it), else the
    // active event's year. Null when neither exists.
    private async Task<int?> GetRegistrationTargetYearAsync(CancellationToken ct)
    {
        var campSettings = await campService.GetSettingsAsync(ct);
        return campSettings.OpenSeasons.Count > 0
            ? campSettings.OpenSeasons.Max()
            : await GetCurrentYearAsync(ct);
    }

    // --- Export ---

    public async Task<string> ExportAsGeoJsonAsync(int year, CancellationToken cancellationToken = default)
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
