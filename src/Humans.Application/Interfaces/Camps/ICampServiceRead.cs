using Humans.Application.DTOs;

namespace Humans.Application.Interfaces.Camps;

/// <summary>
/// Cross-section read surface for the Camps section. External sections inject
/// this interface; it returns only CampInfo-family projections (CampInfo,
/// CampSeasonInfo), CampSettingsInfo, and CampSearchHit — never EF entities.
/// See memory/architecture/section-read-write-split.md.
/// </summary>
public interface ICampServiceRead
{
    Task<IReadOnlyList<CampInfo>> GetCampsForYearAsync(int year, CancellationToken cancellationToken = default);
    Task<CampInfo?> GetCampBySlugAsync(string slug, CancellationToken cancellationToken = default);
    Task<CampSeasonInfo?> GetCampSeasonByIdAsync(Guid campSeasonId, CancellationToken cancellationToken = default);
    Task<CampSettingsInfo> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CampSearchHit>> SearchAsync(string query, int max, CancellationToken cancellationToken = default);
}
