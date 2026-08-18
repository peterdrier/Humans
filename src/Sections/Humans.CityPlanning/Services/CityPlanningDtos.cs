using System.ComponentModel.DataAnnotations;
using NodaTime;

using Humans.Camps.Contracts;
namespace Humans.CityPlanning.Services;

/// <summary>
/// The section's internal service DTOs. <c>CityPlanningSettingsDto</c> is not here — it is
/// the return type of <c>ICityPlanningServiceRead.GetSettingsAsync</c> and lives on the
/// contracts leaf. There is no internal <c>ICityPlanningService</c>: the section's own
/// controllers take the concrete <see cref="CityPlanningService"/> (design §15 step 5 —
/// keep an interface only where something needs the seam), and the cross-section surface is
/// <c>Humans.CityPlanning.Contracts.ICityPlanningService</c>.
/// </summary>
internal sealed record CampPolygonDto(
    Guid CampSeasonId,
    Guid CampId,
    string CampName,
    string CampSlug,
    string GeoJson,
    double AreaSqm,
    SoundZone? SoundZone,
    double? SpaceRequirementSqm);

internal sealed record CampSeasonSummaryDto(
    Guid CampSeasonId,
    string CampName,
    string CampSlug,
    double? SpaceRequirementSqm = null,
    SoundZone? SoundZone = null);

internal sealed record CampPolygonHistoryEntryDto(
    Guid Id,
    string ModifiedByDisplayName,
    Instant ModifiedAt,
    double AreaSqm,
    string Note,
    string GeoJson);

/// <summary>
/// The persisted polygon facts a save/restore returns to callers: the current
/// GeoJson and area. Keeps EF entities (CampPolygon / CampPolygonHistory) from
/// crossing the service boundary into the controllers.
/// </summary>
internal sealed record CampPolygonSaveResult(string GeoJson, double AreaSqm);

internal sealed record PlacementDateUpdateResult(
    bool Success,
    string? ErrorKey = null);

internal sealed record GeoJsonUploadResult(
    bool Success,
    string? ErrorKey = null);

internal sealed record SaveCampPolygonRequest(
    string GeoJson,
    double AreaSqm,
    [StringLength(512, ErrorMessage = "Note cannot exceed 512 characters")] string? Note = null);
