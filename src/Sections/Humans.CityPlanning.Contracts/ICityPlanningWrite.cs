using Humans.Base.Interfaces;

namespace Humans.CityPlanning.Contracts;

/// <summary>
/// The one write City Planning exposes outside itself, alongside the read-only
/// <see cref="ICityPlanningService"/>. Everything else the section's own service does —
/// registration-info edits, polygon save/restore, placement phases, zone uploads, export —
/// is internal to <c>Humans.CityPlanning</c>.
/// </summary>
public interface ICityPlanningWrite : IApplicationService
{
    /// <summary>
    /// Deletes every polygon and history row belonging to the given camp seasons.
    /// Returns the number of rows removed. Called by the Camps section when a Camp
    /// is deleted, before its seasons go; no FK ties these rows to a season, so
    /// nothing else removes them.
    /// </summary>
    Task<int> DeleteCampPolygonsForSeasonsAsync(
        IReadOnlyCollection<Guid> campSeasonIds, CancellationToken cancellationToken = default);
}
