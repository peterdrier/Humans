using Humans.Application.DTOs;

namespace Humans.Application.Interfaces.Camps;

/// <summary>
/// Cross-section read surface for the Camps section. External sections inject
/// this interface for camp read models and settings - never EF entities.
/// See memory/architecture/section-read-write-split.md.
/// </summary>
public interface ICampServiceRead
{
    Task<IReadOnlyList<CampInfo>> GetCampsForYearAsync(int year, CancellationToken cancellationToken = default);
    Task<CampInfo?> GetCampBySlugAsync(string slug, CancellationToken cancellationToken = default);
    Task<CampSeasonInfo?> GetCampSeasonByIdAsync(Guid campSeasonId, CancellationToken cancellationToken = default);
    Task<CampSettingsInfo> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CampSearchHit>> SearchAsync(string query, int max, CancellationToken cancellationToken = default);

    /// <summary>
    /// The user's camp membership for the <b>active (<c>PublicYear</c>) season only</b> —
    /// never a stale prior-year season a camp last ran. <see cref="CampUserInfo.Season"/>
    /// is null (and <see cref="CampUserInfo.Roles"/> empty) when the user is not an
    /// Active member of any camp this year. Served from the cached projection; no DB hit.
    /// Feeds the admin human card and the Shifts coordinator view.
    /// </summary>
    Task<CampUserInfo> GetCampUserInfoAsync(Guid userId, CancellationToken cancellationToken = default);
}
