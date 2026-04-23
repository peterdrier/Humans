using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Shifts section tables owned by
/// <see cref="IShiftManagementService"/>: <c>rotas</c>, <c>shifts</c>,
/// <c>event_settings</c>, <c>shift_tags</c>, <c>volunteer_tag_preferences</c>,
/// and <c>volunteer_event_profiles</c>.
///
/// <para>
/// The <c>shift_signups</c> table is owned long-term by
/// <c>ShiftSignupService</c> (tracked as sub-task <c>#541b</c>). Until that
/// service migrates, this repository also exposes narrow read helpers over
/// signups that the management service needs for summaries, urgency scoring,
/// and dashboard computations. These read helpers intentionally do not
/// mutate signups — writes remain in <c>ShiftSignupService</c>.
/// </para>
///
/// <para>
/// Cross-domain navigation properties (<see cref="Rota.Team"/>,
/// <see cref="ShiftSignup.User"/>) are NEVER eager-loaded here. Consumers
/// that need that data stitch in memory via <c>ITeamService</c> and
/// <c>IUserService</c> at the service layer (design-rules §6b).
/// </para>
///
/// <para>
/// Registered as <b>Singleton</b> — depends on
/// <c>IDbContextFactory&lt;HumansDbContext&gt;</c> and creates short-lived
/// contexts per call.
/// </para>
/// </summary>
public interface IShiftManagementRepository
{
    // ==========================================================================
    // EventSettings
    // ==========================================================================

    /// <summary>Loads the single active <see cref="EventSettings"/>, or null.</summary>
    Task<EventSettings?> GetActiveEventSettingsAsync(CancellationToken ct = default);

    /// <summary>Loads an <see cref="EventSettings"/> by id (read-only).</summary>
    Task<EventSettings?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Returns true if any other <see cref="EventSettings"/> (excluding <paramref name="excludingId"/>) is active.</summary>
    Task<bool> AnyOtherActiveEventSettingsAsync(Guid? excludingId, CancellationToken ct = default);

    /// <summary>Inserts a new <see cref="EventSettings"/>.</summary>
    Task AddEventSettingsAsync(EventSettings entity, CancellationToken ct = default);

    /// <summary>Updates an existing <see cref="EventSettings"/>.</summary>
    Task UpdateEventSettingsAsync(EventSettings entity, CancellationToken ct = default);

    // ==========================================================================
    // Rota
    // ==========================================================================

    /// <summary>Inserts a new rota.</summary>
    Task AddRotaAsync(Rota rota, CancellationToken ct = default);

    /// <summary>Updates an existing rota.</summary>
    Task UpdateRotaAsync(Rota rota, CancellationToken ct = default);

    /// <summary>
    /// Targeted update that only writes the rota's <c>TeamId</c> and
    /// <c>UpdatedAt</c> columns, so concurrent edits to other fields are not
    /// clobbered. Used by the team-move path. Returns <c>false</c> if the
    /// rota does not exist.
    /// </summary>
    Task<bool> UpdateRotaTeamAssignmentAsync(
        Guid rotaId, Guid newTeamId, Instant updatedAt, CancellationToken ct = default);

    /// <summary>
    /// Loads a tracked rota for a team-move operation. Does not include
    /// cross-domain <see cref="Rota.Team"/>. Returns null if not found.
    /// </summary>
    Task<Rota?> GetRotaForUpdateAsync(Guid rotaId, CancellationToken ct = default);

    /// <summary>
    /// Loads a rota with its shifts, all shift signups, and same-section
    /// <see cref="Rota.EventSettings"/> nav, for delete pre-checks. Tracked.
    /// </summary>
    Task<Rota?> GetRotaWithShiftsAndSignupsForDeleteAsync(Guid rotaId, CancellationToken ct = default);

    /// <summary>
    /// Loads a rota with its shifts (same-section nav). Read-only.
    /// <see cref="Rota.Team"/> is NOT populated — callers stitch via <c>ITeamService</c>.
    /// </summary>
    Task<Rota?> GetRotaByIdWithShiftsAsync(Guid rotaId, CancellationToken ct = default);

    /// <summary>
    /// Loads all rotas for a team+event with shifts, shift signups, and tags.
    /// Read-only. Cross-domain navs (<see cref="Rota.Team"/>,
    /// <see cref="ShiftSignup.User"/>) are NOT populated.
    /// </summary>
    Task<IReadOnlyList<Rota>> GetRotasByDepartmentAsync(
        Guid teamId, Guid eventSettingsId, CancellationToken ct = default);

    /// <summary>
    /// Loads a rota with its <see cref="Rota.EventSettings"/> nav (same section).
    /// Tracked so the service can attach new shifts via the context.
    /// </summary>
    Task<Rota?> GetRotaWithEventSettingsAsync(Guid rotaId, CancellationToken ct = default);

    /// <summary>
    /// Removes a rota plus every shift and signup under it in a single save.
    /// The service is expected to have validated "no confirmed signups" first.
    /// </summary>
    Task DeleteRotaCascadeAsync(Guid rotaId, CancellationToken ct = default);

    /// <summary>
    /// Sets the tag membership for a rota, replacing any existing tags. Unknown
    /// tag ids are silently ignored (matches legacy behavior).
    /// </summary>
    Task SetRotaTagsAsync(Guid rotaId, IReadOnlyList<Guid> tagIds, CancellationToken ct = default);

    // ==========================================================================
    // Shift
    // ==========================================================================

    /// <summary>Inserts a new shift.</summary>
    Task AddShiftAsync(Shift shift, CancellationToken ct = default);

    /// <summary>Bulk-inserts shifts in a single save.</summary>
    Task AddShiftsAsync(IEnumerable<Shift> shifts, CancellationToken ct = default);

    /// <summary>Updates an existing shift.</summary>
    Task UpdateShiftAsync(Shift shift, CancellationToken ct = default);

    /// <summary>
    /// Loads a shift with all its signups (same-section nav) for a delete
    /// pre-check. Tracked. Caller is expected to cancel pending signups
    /// via the loaded entities before calling <see cref="DeleteShiftCascadeAsync"/>.
    /// </summary>
    Task<Shift?> GetShiftWithSignupsForDeleteAsync(Guid shiftId, CancellationToken ct = default);

    /// <summary>
    /// Removes a shift plus every signup under it in a single save. Service
    /// validates "no confirmed signups" first.
    /// </summary>
    Task DeleteShiftCascadeAsync(Guid shiftId, CancellationToken ct = default);

    /// <summary>
    /// Loads a shift with its rota, rota's event settings, and all signups.
    /// Read-only. Cross-domain <see cref="Rota.Team"/> nav is NOT populated;
    /// callers stitch team data via <c>ITeamService</c>.
    /// </summary>
    Task<Shift?> GetShiftByIdAsync(Guid shiftId, CancellationToken ct = default);

    /// <summary>
    /// Loads all shifts for a rota with their signups (same-section nav).
    /// Read-only.
    /// </summary>
    Task<IReadOnlyList<Shift>> GetShiftsByRotaAsync(Guid rotaId, CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct day offsets already populated for a rota. Used
    /// by additive bulk-shift generators.
    /// </summary>
    Task<IReadOnlyList<int>> GetShiftDayOffsetsForRotaAsync(Guid rotaId, CancellationToken ct = default);

    // ==========================================================================
    // Reads for dashboards / urgency / staffing
    // ==========================================================================

    /// <summary>
    /// Loads shifts for an event with the Rota nav only (same section). Reads
    /// are <c>AsNoTracking</c>. Cross-domain <see cref="Rota.Team"/> is not
    /// populated.
    /// </summary>
    Task<IReadOnlyList<Shift>> GetShiftsForEventAsync(
        Guid eventSettingsId,
        Guid? departmentId,
        CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="GetShiftsForEventAsync"/> but filters to
    /// <c>!AdminOnly &amp;&amp; Rota.IsVisibleToVolunteers</c> (dashboard).
    /// </summary>
    Task<IReadOnlyList<Shift>> GetVisibleShiftsForEventAsync(
        Guid eventSettingsId,
        CancellationToken ct = default);

    /// <summary>
    /// Loads shifts with their signups and the same-section rota nav.
    /// Read-only. Used by browse-page queries. No cross-domain includes.
    /// </summary>
    Task<IReadOnlyList<Shift>> GetShiftsWithSignupsForEventAsync(
        Guid eventSettingsId,
        Guid? departmentId,
        bool includeAdminOnly,
        bool includeHidden,
        int? fromDayOffset,
        int? toDayOffset,
        bool includeRotaTags,
        CancellationToken ct = default);

    /// <summary>
    /// Loads shifts (with signups) for urgency scoring. Same shape as
    /// <see cref="GetShiftsWithSignupsForEventAsync"/> with a fixed filter
    /// of "in-event, optional department, optional day offset".
    /// </summary>
    Task<IReadOnlyList<Shift>> GetShiftsWithSignupsForUrgencyAsync(
        Guid eventSettingsId,
        Guid? departmentId,
        int? dayOffset,
        int? minDayOffset,
        int? maxDayOffset,
        CancellationToken ct = default);

    /// <summary>
    /// Loads rotas for a team+event with their shifts and signups (same section).
    /// Read-only. Used by <c>GetShiftsSummaryAsync</c>.
    /// </summary>
    Task<IReadOnlyList<Rota>> GetRotasWithShiftsAndSignupsAsync(
        Guid eventSettingsId,
        IReadOnlyList<Guid> teamIds,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct team ids that own rotas in the given event. Used
    /// by <c>GetDepartmentsWithRotasAsync</c>; team names are resolved via
    /// <c>ITeamService</c> at the service layer.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetTeamIdsWithRotasInEventAsync(
        Guid eventSettingsId,
        CancellationToken ct = default);

    /// <summary>
    /// Filters <paramref name="teamIds"/> to those that own at least one rota
    /// with at least one shift in the given event.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetTeamIdsWithShiftsInEventAsync(
        Guid eventSettingsId,
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken ct = default);

    /// <summary>
    /// Confirmed-signup counts grouped by shift id, for a collection of shifts.
    /// Returns an empty dictionary when <paramref name="shiftIds"/> is empty.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetConfirmedSignupCountsByShiftAsync(
        IReadOnlyCollection<Guid> shiftIds,
        CancellationToken ct = default);

    /// <summary>
    /// Distinct user ids with a non-cancelled signup on any of the given shifts.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetEngagedUserIdsForShiftsAsync(
        IReadOnlyCollection<Guid> shiftIds,
        CancellationToken ct = default);

    /// <summary>
    /// Count of pending signups on any of the given shifts whose
    /// <see cref="ShiftSignup.CreatedAt"/> is before <paramref name="staleThreshold"/>.
    /// </summary>
    Task<int> GetStalePendingSignupCountAsync(
        IReadOnlyCollection<Guid> shiftIds,
        Instant staleThreshold,
        CancellationToken ct = default);

    /// <summary>
    /// Pending signup counts grouped by <c>rota.TeamId</c>, deduped by
    /// <c>SignupBlockId ?? Id</c>. Applies an optional day-offset filter
    /// so the caller can scope to a single <see cref="ShiftPeriod"/>.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetPendingSignupCountsByTeamAsync(
        Guid eventSettingsId,
        int? minDayOffset,
        int? maxDayOffset,
        CancellationToken ct = default);

    /// <summary>
    /// Signup <c>CreatedAt</c> timestamps for an event inside a window, with
    /// optional day-offset filter. Used by the trends chart.
    /// </summary>
    Task<IReadOnlyList<Instant>> GetSignupCreatedAtsInWindowAsync(
        Guid eventSettingsId,
        Instant fromInclusive,
        Instant toExclusive,
        int? minDayOffset,
        int? maxDayOffset,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the id of the active event with the given id, or <see cref="Guid.Empty"/>
    /// if no such row exists or it is inactive.
    /// </summary>
    Task<Guid> GetActiveEventIdAsync(Guid eventSettingsId, CancellationToken ct = default);

    // ==========================================================================
    // Shift tags
    // ==========================================================================

    Task<IReadOnlyList<ShiftTag>> GetAllTagsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ShiftTag>> SearchTagsAsync(string query, CancellationToken ct = default);

    /// <summary>
    /// Looks up a tag by case-insensitive name. Returns null if not found.
    /// </summary>
    Task<ShiftTag?> FindTagByNameAsync(string name, CancellationToken ct = default);

    Task AddTagAsync(ShiftTag tag, CancellationToken ct = default);

    // ==========================================================================
    // Volunteer tag preferences
    // ==========================================================================

    Task<IReadOnlyList<ShiftTag>> GetVolunteerTagPreferencesAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Replaces a volunteer's tag preferences with the given tag ids in a
    /// single save (delete-then-insert).
    /// </summary>
    Task SetVolunteerTagPreferencesAsync(
        Guid userId, IReadOnlyList<Guid> tagIds, CancellationToken ct = default);

    // ==========================================================================
    // Volunteer event profiles
    // ==========================================================================

    /// <summary>
    /// Loads a volunteer event profile for a user (tracked). Returns null
    /// if none exists.
    /// </summary>
    Task<VolunteerEventProfile?> GetVolunteerEventProfileForUpdateAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Loads a volunteer event profile (read-only). Returns null if none exists.
    /// </summary>
    Task<VolunteerEventProfile?> GetVolunteerEventProfileAsync(
        Guid userId, CancellationToken ct = default);

    Task AddVolunteerEventProfileAsync(
        VolunteerEventProfile profile, CancellationToken ct = default);

    Task UpdateVolunteerEventProfileAsync(
        VolunteerEventProfile profile, CancellationToken ct = default);
}
