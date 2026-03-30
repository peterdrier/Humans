using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces;

public interface ICampMapService
{
    // Queries
    Task<List<CampPolygonDto>> GetPolygonsAsync(int year, CancellationToken cancellationToken = default);
    Task<List<CampSeasonSummaryDto>> GetCampSeasonsWithoutPolygonAsync(int year, CancellationToken cancellationToken = default);
    Task<List<PolygonHistoryEntryDto>> GetPolygonHistoryAsync(Guid campSeasonId, CancellationToken cancellationToken = default);
    Task<SoundZone?> GetCampSeasonSoundZoneAsync(Guid campSeasonId, CancellationToken cancellationToken = default);
    Task<Guid?> GetUserCampSeasonIdForYearAsync(Guid userId, int year, CancellationToken cancellationToken = default);

    // Writes
    Task<(CampPolygon polygon, CampPolygonHistory history)> SavePolygonAsync(
        Guid campSeasonId, string geoJson, double areaSqm, Guid modifiedByUserId,
        string note = "Saved", CancellationToken cancellationToken = default);

    Task<(CampPolygon polygon, CampPolygonHistory history)> RestorePolygonVersionAsync(
        Guid campSeasonId, Guid historyId, Guid restoredByUserId,
        CancellationToken cancellationToken = default);

    // Authorization
    Task<bool> CanUserEditAsync(Guid userId, Guid campSeasonId, CancellationToken cancellationToken = default);
    Task<bool> IsUserMapAdminAsync(Guid userId, CancellationToken cancellationToken = default);

    // Settings (creates row on demand for PublicYear)
    Task<CampMapSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task OpenPlacementAsync(Guid userId, CancellationToken cancellationToken = default);
    Task ClosePlacementAsync(Guid userId, CancellationToken cancellationToken = default);
    Task UpdateLimitZoneAsync(string geoJson, Guid userId, CancellationToken cancellationToken = default);
    Task DeleteLimitZoneAsync(Guid userId, CancellationToken cancellationToken = default);

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

public record PolygonHistoryEntryDto(
    Guid Id,
    string ModifiedByDisplayName,
    string ModifiedAt,
    double AreaSqm,
    string Note,
    string GeoJson);

public record SavePolygonRequest(string GeoJson, double AreaSqm);
