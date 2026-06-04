using Humans.Domain.Attributes;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Shifts-owned I/O for user-oriented volunteer state:
/// <c>volunteer_build_statuses</c> and <c>general_availability</c>. All methods
/// return materialized lists / nullable rows - no IQueryable leaks.
/// </summary>
[Section("Shifts")]
public interface IVolunteerTrackingRepository : IRepository
{
    /// <summary>
    /// Returns <see cref="VolunteerBuildStatus"/> rows for an event, optionally
    /// restricted to the supplied users. Empty list if no rows match.
    /// </summary>
    Task<IReadOnlyList<VolunteerBuildStatus>> GetBuildStatusesForEventAsync(
        Guid eventSettingsId,
        IReadOnlyCollection<Guid>? userIds = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns general availability rows for an event, optionally restricted to
    /// the supplied users. Empty list if no rows match.
    /// </summary>
    Task<IReadOnlyList<GeneralAvailability>> GetAvailabilityForEventAsync(
        Guid eventSettingsId,
        IReadOnlyCollection<Guid>? userIds = null,
        CancellationToken ct = default);

    /// <summary>Returns general availability rows for a user, optionally restricted to one event.</summary>
    Task<IReadOnlyList<GeneralAvailability>> GetAvailabilityForUserAsync(
        Guid userId,
        Guid? eventSettingsId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Upserts the general availability row for the given user + event pair.
    /// </summary>
    Task UpsertAvailabilityAsync(
        Guid userId,
        Guid eventSettingsId,
        IReadOnlyList<int> dayOffsets,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Account-merge fold for general availability rows.
    /// </summary>
    Task<int> ReassignAvailabilityToUserAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Instant updatedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Upsert (UserId, EventSettingsId): mutate or insert the row's camp set-up
    /// fields and, in the same save, optionally trim day-off entries that the
    /// new camp-setup span newly covers.
    /// <para>
    /// The caller has already validated <paramref name="barrioSetupStartDate"/>
    /// (or null to clear). When <paramref name="setupOffsetThreshold"/> is set,
    /// any <c>DayOffs</c> entries whose <c>DayOffset &gt;= threshold</c> are
    /// removed; pass null to skip the trim.
    /// </para>
    /// <para>Returns the offsets that were trimmed, sorted ascending. Empty
    /// list if no trim happened or no entries matched.</para>
    /// </summary>
    Task<IReadOnlyList<int>> UpsertCampSetupAsync(
        Guid userId,
        Guid eventSettingsId,
        LocalDate? barrioSetupStartDate,
        string? notes,
        Guid? setByUserId,
        Instant? setAt,
        int? setupOffsetThreshold,
        CancellationToken ct = default);

    /// <summary>
    /// Insert or replace a single day-off entry on the row's <c>DayOffs</c>
    /// collection. Creates the row if absent. Replaces any existing entry for
    /// the same <c>DayOffset</c> so there is at most one entry per day.
    /// Persists with the list sorted by <c>DayOffset</c> ascending.
    /// </summary>
    Task UpsertDayOffAsync(
        Guid userId,
        Guid eventSettingsId,
        DayOffEntry entry,
        CancellationToken ct = default);

    /// <summary>
    /// Remove the entry for (userId, eventSettingsId, dayOffset) from the
    /// row's <c>DayOffs</c> collection. Returns whether an entry was actually
    /// removed (false when no row exists or the offset wasn't present).
    /// </summary>
    Task<bool> RemoveDayOffAsync(
        Guid userId,
        Guid eventSettingsId,
        int dayOffset,
        CancellationToken ct = default);

}
