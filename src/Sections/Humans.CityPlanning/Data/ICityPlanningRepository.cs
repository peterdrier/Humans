using Humans.Base.Interfaces.Repositories;
using Humans.CityPlanning.Domain;
using NodaTime;

namespace Humans.CityPlanning.Data;

/// <summary>
/// Reads are <c>AsNoTracking</c>. <see cref="CampPolygonHistory"/> is append-only: no update or delete of a single
/// history row. <see cref="DeletePolygonsForCampSeasonsAsync"/> is the one exception.
/// </summary>
internal interface ICityPlanningRepository : IRepository
{
    /// <summary>Empty input returns an empty list.</summary>
    Task<IReadOnlyList<CampPolygon>> GetPolygonsByCampSeasonIdsAsync(
        IReadOnlyCollection<Guid> campSeasonIds, CancellationToken ct = default);

    /// <summary>The subset of the given ids that already have a polygon row.</summary>
    Task<IReadOnlyList<Guid>> GetCampSeasonIdsWithPolygonAsync(
        IReadOnlyCollection<Guid> campSeasonIds, CancellationToken ct = default);

    /// <summary>In no defined order: the controller sorts for display.</summary>
    Task<IReadOnlyList<CampPolygonHistory>> GetHistoryForCampSeasonAsync(
        Guid campSeasonId, CancellationToken ct = default);

    /// <summary>Null unless the entry belongs to <paramref name="campSeasonId"/>.</summary>
    Task<CampPolygonHistory?> GetHistoryEntryAsync(
        Guid campSeasonId, Guid historyId, CancellationToken ct = default);

    /// <summary>Upserts the polygon and appends a history row in one unit of work. Returns the polygon, detached.</summary>
    Task<CampPolygon> SavePolygonAndAppendHistoryAsync(
        Guid campSeasonId,
        string geoJson,
        double areaSqm,
        Guid modifiedByUserId,
        string note,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// One unit of work; returns rows removed across both tables. No FK ties these rows to a season, so nothing in
    /// the database cascades when one goes.
    /// </summary>
    Task<int> DeletePolygonsForCampSeasonsAsync(
        IReadOnlyCollection<Guid> campSeasonIds, CancellationToken ct = default);

    /// <summary>Detached; null when none exists. Never writes: the read path's lookup.</summary>
    Task<CityPlanningSettings?> GetSettingsAsync(int year, CancellationToken ct = default);

    /// <summary>Creates the row (no override, no schedule) when missing. Write paths only.</summary>
    Task<CityPlanningSettings> GetOrCreateSettingsAsync(
        int year, Instant now, CancellationToken ct = default);

    /// <summary>Creates the row on demand, applies <paramref name="mutate"/>, stamps <c>UpdatedAt</c>, and saves.</summary>
    Task MutateSettingsAsync(
        int year,
        Action<CityPlanningSettings> mutate,
        Instant now,
        CancellationToken ct = default);
}
