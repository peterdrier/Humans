using Humans.Base.Interfaces;

namespace Humans.CityPlanning.Contracts;

public interface ICityPlanningWrite : IApplicationService
{
    /// <summary>
    /// Returns rows removed. Camps calls this before deleting a camp's seasons: no FK ties these rows to a
    /// season, so nothing else removes them.
    /// </summary>
    Task<int> DeleteCampPolygonsForSeasonsAsync(
        IReadOnlyCollection<Guid> campSeasonIds, CancellationToken cancellationToken = default);
}
