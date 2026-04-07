using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces;

public interface ICityPlanningService
{
    // Queries
    Task<List<CampPolygonDto>> GetCampPolygonsAsync(int year, CancellationToken cancellationToken = default);
    Task<List<CampSeasonSummaryDto>> GetCampSeasonsWithoutCampPolygonAsync(int year, CancellationToken cancellationToken = default);
    Task<List<CampPolygonHistoryEntryDto>> GetCampPolygonHistoryAsync(Guid campSeasonId, CancellationToken cancellationToken = default);
    Task<SoundZone?> GetCampSeasonSoundZoneAsync(Guid campSeasonId, CancellationToken cancellationToken = default);
    Task<string?> GetCampSeasonNameAsync(Guid campSeasonId, CancellationToken cancellationToken = default);
    Task<Guid?> GetUserCampSeasonIdForYearAsync(Guid userId, int year, CancellationToken cancellationToken = default);
    Task<string?> GetUserDisplayNameAsync(Guid userId, CancellationToken cancellationToken = default);

    // Writes
    Task<(CampPolygon polygon, CampPolygonHistory history)> SaveCampPolygonAsync(
        Guid campSeasonId, string geoJson, double areaSqm, Guid modifiedByUserId,
        string note = "Saved", CancellationToken cancellationToken = default);

    Task<(CampPolygon polygon, CampPolygonHistory history)> RestoreCampPolygonVersionAsync(
        Guid campSeasonId, Guid historyId, Guid restoredByUserId,
        CancellationToken cancellationToken = default);

    // Authorization (global role checks belong at the controller level via claims)
    Task<bool> CanUserEditAsync(Guid userId, Guid campSeasonId, CancellationToken cancellationToken = default);
    Task<bool> IsCityPlanningTeamMemberAsync(Guid userId, CancellationToken cancellationToken = default);

    // Settings (creates row on demand for PublicYear)
    Task<CityPlanningSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task OpenPlacementAsync(Guid userId, CancellationToken cancellationToken = default);
    Task ClosePlacementAsync(Guid userId, CancellationToken cancellationToken = default);
    Task UpdateLimitZoneAsync(string geoJson, Guid userId, CancellationToken cancellationToken = default);
    Task DeleteLimitZoneAsync(Guid userId, CancellationToken cancellationToken = default);
    Task UpdateOfficialZonesAsync(string geoJson, Guid userId, CancellationToken cancellationToken = default);
    Task DeleteOfficialZonesAsync(Guid userId, CancellationToken cancellationToken = default);
    Task UpdatePlacementDatesAsync(LocalDateTime? opensAt, LocalDateTime? closesAt, CancellationToken cancellationToken = default);

    // Export
    Task<string> ExportAsGeoJsonAsync(int year, CancellationToken cancellationToken = default);
}

public record CampPolygonDto(
    Guid CampSeasonId,
    string CampName,
    string CampSlug,
    string GeoJson,
    double AreaSqm,
    SoundZone? SoundZone);

public record CampSeasonSummaryDto(
    Guid CampSeasonId,
    string CampName,
    string CampSlug);

public record CampPolygonHistoryEntryDto(
    Guid Id,
    string ModifiedByDisplayName,
    string ModifiedAt,
    double AreaSqm,
    string Note,
    string GeoJson);

public record SaveCampPolygonRequest(string GeoJson, double AreaSqm);
