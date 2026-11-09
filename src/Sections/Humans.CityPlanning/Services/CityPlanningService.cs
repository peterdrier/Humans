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
    private readonly CityPlanningReader planningReader = new(repo, clock, options, campService, teamService, userService, settingsService);

    private const long MaxGeoJsonUploadBytes = 10 * 1024 * 1024;

    private async Task<int> RequireCurrentYearAsync(CancellationToken cancellationToken) =>
        await planningReader.GetCurrentYearAsync(cancellationToken)
        ?? throw new InvalidOperationException("No active event: city planning has no current year to write to.");

    /// <summary>
    /// The actor exists nowhere but the audit log, so every settings change records it here.
    /// </summary>
    /// <remarks>
    /// <c>LogAsync</c> takes no cancellation token, so nothing cancellable may sit between the commit and the audit
    /// call: the row id is resolved before the write, and the write runs on <see cref="CancellationToken.None"/>. A
    /// request aborted mid-<c>SaveChangesAsync</c> can commit the row while the await reports cancellation, landing the
    /// change without its actor. Rows are created on demand and never deleted, so the id read here is the one written.
    /// </remarks>
    private async Task MutateSettingsAndAuditAsync(
        Action<CityPlanningSettings, Instant> mutate,
        AuditActionDefinition action,
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

    public Task<List<CampPolygonDto>> GetCampPolygonsAsync(
        int year, CancellationToken cancellationToken = default) =>
        planningReader.GetCampPolygonsAsync(year, cancellationToken);

    public Task<List<CampSeasonSummaryDto>> GetCampSeasonsWithoutCampPolygonAsync(
        int year, CancellationToken cancellationToken = default) =>
        planningReader.GetCampSeasonsWithoutCampPolygonAsync(year, cancellationToken);

    public Task<List<CampPolygonHistoryEntryDto>> GetCampPolygonHistoryAsync(
        Guid campSeasonId, CancellationToken cancellationToken = default) =>
        planningReader.GetCampPolygonHistoryAsync(campSeasonId, cancellationToken);

    public async Task<CampPolygonSaveResult> SaveCampPolygonAsync(
        Guid campSeasonId, string geoJson, double areaSqm, Guid modifiedByUserId,
        string note = "Saved", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(geoJson) || !CityPlanningGeoJsonValidation.IsValidCampGeometry(geoJson))
            return new(string.Empty, 0, Refusal: "Invalid GeoJSON.");
        if (!double.IsFinite(areaSqm) || areaSqm < 0)
            return new(string.Empty, 0, Refusal: "Area must be finite and non-negative.");

        if (await campService.GetCampSeasonByIdAsync(campSeasonId, cancellationToken) is null)
            return new(string.Empty, 0, Refusal: "Camp season not found.");

        var now = clock.GetCurrentInstant();
        var polygon = await repo.SavePolygonAndAppendHistoryAsync(
            campSeasonId, geoJson, areaSqm, modifiedByUserId, note, now, cancellationToken);
        return new CampPolygonSaveResult(polygon.GeoJson, polygon.AreaSqm);
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

    public Task<bool> IsCityPlanningTeamMemberAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        planningReader.IsCityPlanningTeamMemberAsync(userId, cancellationToken);

    public Task<bool> CanUserEditAsync(
        Guid userId, Guid campSeasonId, CancellationToken cancellationToken = default) =>
        planningReader.CanUserEditAsync(userId, campSeasonId, cancellationToken);

    public Task<CityPlanningSettingsDto?> GetSettingsAsync(
        CancellationToken cancellationToken = default, int? year = null) =>
        planningReader.GetSettingsAsync(cancellationToken, year);

    public Task OpenPlacementAsync(Guid userId, CancellationToken cancellationToken = default) =>
        MutateSettingsAndAuditAsync(
            (s, now) =>
            {
                s.PlacementOverride = true;
                s.OpenedAt = now;
            },
            CityPlanningAuditActions.CityPlanningPlacementOpened,
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
            CityPlanningAuditActions.CityPlanningPlacementClosed,
            "Forced barrio placement closed (manual override of the schedule)",
            userId,
            cancellationToken);

    public Task ClearPlacementOverrideAsync(Guid userId, CancellationToken cancellationToken = default) =>
        MutateSettingsAndAuditAsync(
            (s, _) => s.PlacementOverride = null,
            CityPlanningAuditActions.CityPlanningPlacementOverrideCleared,
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
            CityPlanningAuditActions.CityPlanningContainerPlacementOpened,
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
            CityPlanningAuditActions.CityPlanningContainerPlacementClosed,
            "Closed container placement",
            userId,
            cancellationToken);

    public Task UpdateLimitZoneAsync(
        string geoJson, Guid userId, CancellationToken cancellationToken = default) =>
        MutateSettingsAndAuditAsync(
            (s, _) => s.LimitZoneGeoJson = geoJson,
            CityPlanningAuditActions.CityPlanningLimitZoneUpdated,
            "Uploaded the site boundary",
            userId,
            cancellationToken);

    public Task<GeoJsonUploadResult> UpdateLimitZoneFromUploadAsync(
        IFormFile? file, Guid userId, CancellationToken cancellationToken = default) =>
        UploadGeoJsonAsync(file, userId, UpdateLimitZoneAsync, cancellationToken);

    private static async Task<GeoJsonUploadResult> UploadGeoJsonAsync(
        IFormFile? file, Guid userId,
        Func<string, Guid, CancellationToken, Task> updateAsync,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return new(false, "MissingFile");

        if (file.Length > MaxGeoJsonUploadBytes)
            return new(false, "FileTooLarge");

        string geoJson;
        using (var reader = new StreamReader(file.OpenReadStream()))
        {
            geoJson = await reader.ReadToEndAsync(cancellationToken);
            if (!CityPlanningGeoJsonValidation.IsValidOverlayGeoJson(geoJson))
                return new(false, "InvalidGeoJson");
        }

        await updateAsync(geoJson, userId, cancellationToken);
        return new(true);
    }

    public Task DeleteLimitZoneAsync(Guid userId, CancellationToken cancellationToken = default) =>
        MutateSettingsAndAuditAsync(
            (s, _) => s.LimitZoneGeoJson = null,
            CityPlanningAuditActions.CityPlanningLimitZoneDeleted,
            "Deleted the site boundary",
            userId,
            cancellationToken);

    public Task UpdateOfficialZonesAsync(
        string geoJson, Guid userId, CancellationToken cancellationToken = default) =>
        MutateSettingsAndAuditAsync(
            (s, _) => s.OfficialZonesGeoJson = geoJson,
            CityPlanningAuditActions.CityPlanningOfficialZonesUpdated,
            "Uploaded the official zones",
            userId,
            cancellationToken);

    public Task<GeoJsonUploadResult> UpdateOfficialZonesFromUploadAsync(
        IFormFile? file, Guid userId, CancellationToken cancellationToken = default) =>
        UploadGeoJsonAsync(file, userId, UpdateOfficialZonesAsync, cancellationToken);

    public Task DeleteOfficialZonesAsync(Guid userId, CancellationToken cancellationToken = default) =>
        MutateSettingsAndAuditAsync(
            (s, _) => s.OfficialZonesGeoJson = null,
            CityPlanningAuditActions.CityPlanningOfficialZonesDeleted,
            "Deleted the official zones",
            userId,
            cancellationToken);

    /// <summary>Furthest a schedule day may sit from gate opening, either side.</summary>
    internal const int MaxPlacementOffsetDays = 366;

    /// <summary>
    /// Null clears that marker. The days must run open ≤ close ≤ changeable-until, and a changeable-until day needs a
    /// closing day.
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
            CityPlanningAuditActions.CityPlanningPlacementDatesUpdated,
            $"Set the barrio placement schedule (days from gate opening): opens {FormatOffset(opensOffset)}, " +
            $"closes {FormatOffset(closesOffset)}, changeable until {FormatOffset(changeableUntilOffset)}",
            userId,
            cancellationToken);
        return new PlacementScheduleUpdateResult(true);
    }

    private static string FormatOffset(int? offset) =>
        offset is { } days ? days.ToString("+0;-0;0", CultureInfo.InvariantCulture) : "unset";

    public Task<string?> GetRegistrationInfoAsync(CancellationToken cancellationToken = default) =>
        planningReader.GetRegistrationInfoAsync(cancellationToken);

    public async Task UpdateRegistrationInfoAsync(
        string? registrationInfo, Guid userId, CancellationToken cancellationToken = default)
    {
        var targetYear = await planningReader.GetRegistrationTargetYearAsync(cancellationToken)
            ?? throw new InvalidOperationException("No open season and no active event: registration info has no year to write to.");
        var trimmed = string.IsNullOrWhiteSpace(registrationInfo) ? null : registrationInfo.Trim();
        var now = clock.GetCurrentInstant();
        // Same ordering as MutateSettingsAndAuditAsync, but keyed to the registration target year.
        var settings = await repo.GetOrCreateSettingsAsync(targetYear, now, cancellationToken);
        await repo.MutateSettingsAsync(
            targetYear, s => s.RegistrationInfo = trimmed, now, CancellationToken.None);
        await auditLog.LogAsync(
            CityPlanningAuditActions.CityPlanningRegistrationInfoUpdated, nameof(CityPlanningSettings), settings.Id,
            trimmed is null ? $"Cleared the {targetYear} registration info" : $"Updated the {targetYear} registration info",
            userId);
    }

    public Task<string> ExportAsGeoJsonAsync(int year, CancellationToken cancellationToken = default) =>
        planningReader.ExportAsGeoJsonAsync(year, cancellationToken);
}
