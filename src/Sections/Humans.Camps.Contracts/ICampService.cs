
using Humans.Base.Interfaces;

namespace Humans.Camps.Contracts;

public interface ICampService : IApplicationService
{
    Task<IReadOnlyList<CampInfo>> GetCampsForYearAsync(int year, CancellationToken cancellationToken = default);
    Task<CampInfo?> GetCampBySlugAsync(string slug, CancellationToken cancellationToken = default);

    Task<CampInfo?> GetCampByIdAsync(Guid campId, CancellationToken cancellationToken = default);
    Task<CampSeasonInfo?> GetCampSeasonByIdAsync(Guid campSeasonId, CancellationToken cancellationToken = default);
    Task<CampSettingsInfo> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CampSearchHit>> SearchAsync(string query, int max, CancellationToken cancellationToken = default);

    /// <summary>
    /// The active (<c>PublicYear</c>) season only, never a prior year's. <see cref="CampUserInfo.Season"/> is null
    /// and <see cref="CampUserInfo.Roles"/> empty when the user is not an Active member of any camp this year.
    /// </summary>
    Task<CampUserInfo> GetCampUserInfoAsync(Guid userId, CancellationToken cancellationToken = default);
}
