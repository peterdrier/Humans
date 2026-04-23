using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the City Planning section's owned tables:
/// <c>city_planning_settings</c>, <c>camp_polygons</c>, and
/// <c>camp_polygon_histories</c>. The only non-test file that may write to or
/// query those DbSets after the City Planning migration lands.
/// </summary>
/// <remarks>
/// Read methods are <c>AsNoTracking</c>. Per <see cref="Humans.Domain.Entities.CampPolygonHistory"/>'s
/// append-only invariant (design-rules §12), this repository exposes only
/// <c>Add</c>/<c>Get</c> for history rows — no <c>UpdateAsync</c> or
/// <c>DeleteAsync</c>. Restores write a new history row and update the
/// corresponding <see cref="Humans.Domain.Entities.CampPolygon"/>.
/// </remarks>
public interface ICityPlanningRepository
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
    /// Returns all history entries for a single camp season, ordered by
    /// <c>ModifiedAt</c> descending (most recent first). Read-only (AsNoTracking).
    /// The returned rows carry only the FK <c>ModifiedByUserId</c> — user display
    /// data must be resolved through <c>IUserService</c> at the service layer.
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
    /// Returns the persisted polygon and history entities (detached).
    /// </summary>
    Task<(CampPolygon polygon, CampPolygonHistory history)> SavePolygonAndAppendHistoryAsync(
        Guid campSeasonId,
        string geoJson,
        double areaSqm,
        Guid modifiedByUserId,
        string note,
        Instant now,
        CancellationToken ct = default);

    // ==========================================================================
    // Reads / Writes — CityPlanningSettings
    // ==========================================================================

    /// <summary>
    /// Returns the <see cref="CityPlanningSettings"/> row for the given year,
    /// or <c>null</c> if none exists. Read-only (AsNoTracking).
    /// </summary>
    Task<CityPlanningSettings?> GetSettingsByYearAsync(int year, CancellationToken ct = default);

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
    /// and persists. Returns the updated row (detached).
    /// </summary>
    Task<CityPlanningSettings> MutateSettingsAsync(
        int year,
        Action<CityPlanningSettings> mutate,
        Instant now,
        CancellationToken ct = default);
}
