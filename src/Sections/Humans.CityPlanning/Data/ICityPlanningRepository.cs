using Humans.Base.Interfaces.Repositories;
using Humans.CityPlanning.Domain;
using NodaTime;

namespace Humans.CityPlanning.Data;

/// <summary>
/// Repository for the City Planning section's owned tables:
/// <c>city_planning_settings</c>, <c>camp_polygons</c>, and
/// <c>camp_polygon_histories</c>. The only non-test file that may write to or
/// query those DbSets.
/// </summary>
/// <remarks>
/// Read methods are <c>AsNoTracking</c>. Per <see cref="CampPolygonHistory"/>'s
/// append-only invariant (design-rules §12), this repository exposes no update or
/// delete for a single history row; restores write a new one and update the
/// corresponding <see cref="CampPolygon"/>. The season-scoped
/// <see cref="DeletePolygonsForCampSeasonsAsync"/> is the one exception, and it
/// removes the polygon with the history.
/// </remarks>
internal interface ICityPlanningRepository : IRepository
{
    // ==========================================================================
    // Reads — CampPolygon
    // ==========================================================================

    /// <summary>
    /// Returns every camp polygon whose <c>CampSeasonId</c> is in the given
    /// collection. Read-only (AsNoTracking). Empty input returns an empty list.
    /// </summary>
    Task<IReadOnlyList<CampPolygon>> GetPolygonsByCampSeasonIdsAsync(
        IReadOnlyCollection<Guid> campSeasonIds, CancellationToken ct = default);

    /// <summary>
    /// Returns the subset of the given camp season ids that already have a
    /// polygon row. Read-only (AsNoTracking).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetCampSeasonIdsWithPolygonAsync(
        IReadOnlyCollection<Guid> campSeasonIds, CancellationToken ct = default);

    // ==========================================================================
    // Reads — CampPolygonHistory
    // ==========================================================================

    /// <summary>
    /// Returns all history entries for a single camp season, in no defined order —
    /// display ordering belongs to the controller
    /// (<c>memory/architecture/display-sort-in-controllers.md</c>). Read-only
    /// (AsNoTracking). The returned rows carry only the FK <c>ModifiedByUserId</c>;
    /// user display data is resolved through <c>IUserServiceRead</c> at the service
    /// layer.
    /// </summary>
    Task<IReadOnlyList<CampPolygonHistory>> GetHistoryForCampSeasonAsync(
        Guid campSeasonId, CancellationToken ct = default);

    /// <summary>
    /// Returns the history entry identified by <paramref name="historyId"/> if and
    /// only if it belongs to <paramref name="campSeasonId"/>. Read-only
    /// (AsNoTracking). Returns <c>null</c> when no match exists.
    /// </summary>
    Task<CampPolygonHistory?> GetHistoryEntryAsync(
        Guid campSeasonId, Guid historyId, CancellationToken ct = default);

    // ==========================================================================
    // Writes — CampPolygon + CampPolygonHistory (atomic upsert + history append)
    // ==========================================================================

    /// <summary>
    /// Upserts the <see cref="CampPolygon"/> for the given camp season and appends
    /// a new <see cref="CampPolygonHistory"/> row in the same unit of work.
    /// Returns the persisted polygon (detached); the appended history row is a side
    /// effect, readable via <see cref="GetHistoryForCampSeasonAsync"/>.
    /// </summary>
    Task<CampPolygon> SavePolygonAndAppendHistoryAsync(
        Guid campSeasonId,
        string geoJson,
        double areaSqm,
        Guid modifiedByUserId,
        string note,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes every polygon and history row belonging to the given camp seasons,
    /// in one unit of work. Returns the number of rows removed across both tables.
    /// Empty input is a no-op returning 0.
    /// </summary>
    /// <remarks>
    /// This section's tables carry no FK to <c>CampSeason</c> — the column is a bare
    /// <c>Guid</c> — so nothing in the database cascades when a camp season goes.
    /// Camps calls this through <c>ICityPlanningService</c> inside its own delete,
    /// never repository-to-repository.
    /// </remarks>
    Task<int> DeletePolygonsForCampSeasonsAsync(
        IReadOnlyCollection<Guid> campSeasonIds, CancellationToken ct = default);

    // ==========================================================================
    // Reads / Writes — CityPlanningSettings
    // ==========================================================================

    /// <summary>
    /// Returns the <see cref="CityPlanningSettings"/> row for the given year,
    /// creating a new one (with <c>IsPlacementOpen = false</c>) if it does not
    /// exist yet. Always returns a detached, up-to-date row.
    /// </summary>
    Task<CityPlanningSettings> GetOrCreateSettingsAsync(
        int year, Instant now, CancellationToken ct = default);

    /// <summary>
    /// Loads the settings row for the given year (creating on demand), applies
    /// <paramref name="mutate"/>, sets <c>UpdatedAt</c> to <paramref name="now"/>,
    /// and persists. Read the result back through
    /// <see cref="GetOrCreateSettingsAsync"/> when a caller needs it.
    /// </summary>
    Task MutateSettingsAsync(
        int year,
        Action<CityPlanningSettings> mutate,
        Instant now,
        CancellationToken ct = default);
}
