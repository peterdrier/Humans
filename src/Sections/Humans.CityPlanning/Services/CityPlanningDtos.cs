using System.ComponentModel.DataAnnotations;
using NodaTime;

using Humans.Camps.Contracts;
namespace Humans.CityPlanning.Services;

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

internal sealed record CampPolygonSaveResult(string GeoJson, double AreaSqm);

internal sealed record PlacementScheduleUpdateResult(
    bool Success,
    string? ErrorKey = null);

internal sealed record GeoJsonUploadResult(
    bool Success,
    string? ErrorKey = null);

internal sealed record SaveCampPolygonRequest(
    string GeoJson,
    double AreaSqm,
    [StringLength(512, ErrorMessage = "Note cannot exceed 512 characters")] string? Note = null);
