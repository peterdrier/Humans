using System.ComponentModel.DataAnnotations;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Http;
using NodaTime;

namespace Humans.Application.Interfaces.CityPlanning;

public interface ICityPlanningService : ICityPlanningServiceRead, IApplicationService
{
    // Queries
    Task<List<CampPolygonDto>> GetCampPolygonsAsync(int year, CancellationToken cancellationToken = default);
    Task<List<CampSeasonSummaryDto>> GetCampSeasonsWithoutCampPolygonAsync(int year, CancellationToken cancellationToken = default);
    Task<List<CampPolygonHistoryEntryDto>> GetCampPolygonHistoryAsync(Guid campSeasonId, CancellationToken cancellationToken = default);

    // Writes
    Task<CampPolygonSaveResult> SaveCampPolygonAsync(
        Guid campSeasonId, string geoJson, double areaSqm, Guid modifiedByUserId,
        string note = "Saved", CancellationToken cancellationToken = default);

    Task<CampPolygonSaveResult> RestoreCampPolygonVersionAsync(
        Guid campSeasonId, Guid historyId, Guid restoredByUserId,
        CancellationToken cancellationToken = default);

    // Authorization (global role checks belong at the controller level via claims)
    // IsCityPlanningTeamMemberAsync is inherited from ICityPlanningServiceRead.
    Task<bool> CanUserEditAsync(Guid userId, Guid campSeasonId, CancellationToken cancellationToken = default);

    // Settings (GetSettingsAsync is inherited from ICityPlanningServiceRead; creates row on demand for PublicYear)
    Task OpenPlacementAsync(Guid userId, CancellationToken cancellationToken = default);
    Task ClosePlacementAsync(Guid userId, CancellationToken cancellationToken = default);
    Task OpenContainerPlacementAsync(Guid userId, CancellationToken cancellationToken = default);
    Task CloseContainerPlacementAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<GeoJsonUploadResult> UpdateLimitZoneFromUploadAsync(IFormFile? file, Guid userId, CancellationToken cancellationToken = default);
    Task UpdateLimitZoneAsync(string geoJson, Guid userId, CancellationToken cancellationToken = default);
    Task DeleteLimitZoneAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<GeoJsonUploadResult> UpdateOfficialZonesFromUploadAsync(IFormFile? file, Guid userId, CancellationToken cancellationToken = default);
    Task UpdateOfficialZonesAsync(string geoJson, Guid userId, CancellationToken cancellationToken = default);
    Task DeleteOfficialZonesAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<PlacementDateUpdateResult> UpdatePlacementDatesAsync(string? opensAt, string? closesAt, CancellationToken cancellationToken = default);
    // GetRegistrationInfoAsync is inherited from ICityPlanningServiceRead.
    Task UpdateRegistrationInfoAsync(string? registrationInfo, CancellationToken cancellationToken = default);

    // Export
    Task<string> ExportAsGeoJsonAsync(int year, CancellationToken cancellationToken = default);
}

public record CampPolygonDto(
    Guid CampSeasonId,
    Guid CampId,
    string CampName,
    string CampSlug,
    string GeoJson,
    double AreaSqm,
    SoundZone? SoundZone,
    double? SpaceRequirementSqm);

public record CampSeasonSummaryDto(
    Guid CampSeasonId,
    string CampName,
    string CampSlug,
    double? SpaceRequirementSqm = null,
    SoundZone? SoundZone = null);

public record CampPolygonHistoryEntryDto(
    Guid Id,
    string ModifiedByDisplayName,
    Instant ModifiedAt,
    double AreaSqm,
    string Note,
    string GeoJson);

/// <summary>
/// The persisted polygon facts a save/restore returns to callers: the current
/// GeoJson and area. Keeps EF entities (CampPolygon / CampPolygonHistory) from
/// crossing the service boundary into the Web layer.
/// </summary>
public sealed record CampPolygonSaveResult(string GeoJson, double AreaSqm);

public record CityPlanningSettingsDto(
    Guid Id,
    int Year,
    bool IsPlacementOpen,
    Instant? OpenedAt,
    Instant? ClosedAt,
    LocalDateTime? PlacementOpensAt,
    LocalDateTime? PlacementClosesAt,
    string? RegistrationInfo,
    string? LimitZoneGeoJson,
    string? OfficialZonesGeoJson,
    bool IsContainerPlacementOpen,
    Instant? ContainerPlacementOpenedAt,
    Instant? ContainerPlacementClosedAt,
    Instant UpdatedAt);

public sealed record PlacementDateUpdateResult(
    bool Success,
    string? ErrorKey = null);

public sealed record GeoJsonUploadResult(
    bool Success,
    string? ErrorKey = null);

public record SaveCampPolygonRequest(
    string GeoJson,
    double AreaSqm,
    [StringLength(512, ErrorMessage = "Note cannot exceed 512 characters")] string? Note = null);
