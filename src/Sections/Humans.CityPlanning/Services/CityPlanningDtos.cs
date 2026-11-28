using JetBrains.Annotations;
using System.ComponentModel.DataAnnotations;
using NodaTime;

using Humans.Camps.Contracts;
namespace Humans.CityPlanning.Services;

[UsedImplicitly(ImplicitUseKindFlags.Access, ImplicitUseTargetFlags.WithMembers)]
internal sealed record CampPolygonDto(
    Guid CampSeasonId,
    Guid CampId,
    string CampName,
    string CampSlug,
    string GeoJson,
    double AreaSqm,
    SoundZone? SoundZone,
    double? SpaceRequirementSqm);

[UsedImplicitly(ImplicitUseKindFlags.Access, ImplicitUseTargetFlags.WithMembers)]
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

internal sealed record CampPolygonSaveResult(string GeoJson, double AreaSqm, string? Refusal = null);

internal sealed record PlacementScheduleUpdateResult(
    bool Success,
    string? ErrorKey = null);

internal sealed record GeoJsonUploadResult(
    bool Success,
    string? ErrorKey = null);

internal sealed record SaveCampPolygonRequest(
    string GeoJson,
    double AreaSqm,
    [StringLength(512, ErrorMessage = "Validation_MaxLength")] string? Note = null);

/// <summary>
/// <c>PlacementState</c> is the window in effect now: <c>PlacementOverride</c> when set (true = forced open,
/// false = forced closed), else the schedule in the active event's time zone. <c>OpenedAt</c>/<c>ClosedAt</c>
/// are when an admin last forced it. The <c>*Offset</c> values are days from gate opening (negative = before):
/// first open day, last open day, last day a lead may adjust an existing polygon. The matching <c>*On</c>
/// dates are null when the offset is unset or the row's year is not the active event's.
/// </summary>
internal sealed record CityPlanningSettingsDto(
    Guid Id,
    int Year,
    BarrioPlacementState PlacementState,
    bool? PlacementOverride,
    Instant? OpenedAt,
    Instant? ClosedAt,
    int? PlacementOpensOffset,
    int? PlacementClosesOffset,
    int? PlacementChangeableUntilOffset,
    LocalDate? PlacementOpensOn,
    LocalDate? PlacementClosesOn,
    LocalDate? PlacementChangeableUntilOn,
    string? LimitZoneGeoJson,
    string? OfficialZonesGeoJson,
    bool IsContainerPlacementOpen,
    Instant? ContainerPlacementOpenedAt,
    Instant? ContainerPlacementClosedAt);

/// <summary>
/// Where the barrio placement window stands for a camp lead. City-planning team members and
/// CampAdmin/Admin edit regardless.
/// </summary>
internal enum BarrioPlacementState
{
    /// <summary>No lead may create or change a polygon.</summary>
    Closed,

    /// <summary>A lead may place their own camp season's polygon and edit it.</summary>
    Open,

    /// <summary>A lead may still adjust their existing polygon but not create a new one.</summary>
    Changeable,
}
