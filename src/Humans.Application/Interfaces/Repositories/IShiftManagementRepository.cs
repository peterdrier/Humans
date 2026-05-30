using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using Humans.Domain.Attributes;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Shifts section tables owned by
/// <see cref="IShiftManagementService"/>: <c>rotas</c>, <c>shifts</c>,
/// <c>event_settings</c>, <c>shift_tags</c>, <c>volunteer_tag_preferences</c>,
/// and <c>volunteer_event_profiles</c>.
///
/// <para>
/// Signup state-machine reads and writes are declared on the signup-focused
/// partial declaration of this interface so Shifts has one repository contract
/// backed by one concrete persistence adapter.
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
/// Implemented by the scoped <c>ShiftRepository</c>. Management methods create
/// short-lived contexts per call.
/// </para>
/// </summary>
[Section("Shifts")]
public partial interface IShiftManagementRepository : IRepository
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

    /// <summary>Inserts or updates an <see cref="EventSettings"/>.</summary>
    Task SaveEventSettingsAsync(EventSettings entity, EntityMutationMode mode, CancellationToken ct = default);

    /// <summary>
    /// Deletes an <see cref="EventSettings"/> row and all Shifts-owned rows beneath it.
    /// Returns the number of event rows deleted.
    /// </summary>
    Task<int> DeleteEventCascadeAsync(Guid eventSettingsId, CancellationToken ct = default);

    // ==========================================================================
    // Rota
    // ==========================================================================

    /// <summary>Inserts or updates a rota.</summary>
    Task SaveRotaAsync(Rota rota, EntityMutationMode mode, CancellationToken ct = default);

    /// <summary>
    /// Targeted update that only writes the rota's <c>TeamId</c> and
    /// <c>UpdatedAt</c> columns, so concurrent edits to other fields are not
    /// clobbered. Used by the team-move path. Returns <c>false</c> if the
    /// rota does not exist.
    /// </summary>
    Task<bool> UpdateRotaTeamAssignmentAsync(
        Guid rotaId, Guid newTeamId, Instant updatedAt, CancellationToken ct = default);

    /// <summary>
    /// Loads one rota with an explicit same-section include shape. Read-only.
    /// Cross-domain <see cref="Rota.Team"/> is NOT populated; callers stitch
    /// via <c>ITeamService</c>.
    /// </summary>
    Task<Rota?> GetRotaAsync(Guid rotaId, RotaReadShape shape, CancellationToken ct = default);

    /// <summary>
    /// Removes a rota plus every shift and signup under it in a single save.
    /// The service is expected to have validated "no confirmed signups" first.
    /// </summary>
    Task DeleteRotaCascadeAsync(Guid rotaId, CancellationToken ct = default);

    /// <summary>
    /// Volunteer-visible rotas in the active event whose <c>Name</c> contains
    /// <paramref name="query"/> (case-insensitive, Postgres ILike). Capped at
    /// <paramref name="max"/>; ordering is unspecified (caller ranks).
    /// Read-only, no cross-domain navs — caller stitches team display data
    /// via <c>ITeamService</c>.
    /// </summary>
    Task<IReadOnlyList<Rota>> SearchVolunteerVisibleRotasAsync(
        string query, Guid eventSettingsId, int max, CancellationToken ct = default);

    /// <summary>
    /// Sets the tag membership for a rota, replacing any existing tags. Unknown
    /// tag ids are silently ignored (matches legacy behavior).
    /// </summary>
    Task SetRotaTagsAsync(Guid rotaId, IReadOnlyList<Guid> tagIds, CancellationToken ct = default);

    // ==========================================================================
    // Shift
    // ==========================================================================

    /// <summary>Inserts or updates a shift.</summary>
    Task SaveShiftAsync(Shift shift, EntityMutationMode mode, CancellationToken ct = default);

    /// <summary>Bulk-inserts shifts in a single save.</summary>
    Task AddShiftsAsync(IEnumerable<Shift> shifts, CancellationToken ct = default);

    /// <summary>
    /// Loads one shift with an explicit same-section include shape. Read-only.
    /// Cross-domain navs are NOT populated; callers stitch those via section services.
    /// </summary>
    Task<Shift?> GetShiftAsync(Guid shiftId, ShiftReadShape shape, CancellationToken ct = default);

    /// <summary>
    /// Removes a shift plus every signup under it in a single save. Service
    /// validates "no confirmed signups" first.
    /// </summary>
    Task DeleteShiftCascadeAsync(Guid shiftId, CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct day offsets already populated for a rota. Used
    /// by additive bulk-shift generators.
    /// </summary>
    Task<IReadOnlyList<int>> GetShiftDayOffsetsForRotaAsync(Guid rotaId, CancellationToken ct = default);

    // ==========================================================================
    // Reads for dashboards / urgency / staffing
    // ==========================================================================

    /// <summary>
    /// Loads event-scoped shifts with the same-section rota nav. Optional flags
    /// control volunteer-visible filtering and same-section eager loads; no
    /// cross-domain navs are populated.
    /// </summary>
    Task<IReadOnlyList<Shift>> GetEventShiftsAsync(
        ShiftEventQuery query,
        CancellationToken ct = default);

    /// <summary>
    /// Loads rotas for the supplied teams and event with an explicit
    /// same-section include shape. Read-only; cross-domain navs are not
    /// populated.
    /// </summary>
    Task<IReadOnlyList<Rota>> GetRotasAsync(
        Guid eventSettingsId,
        IReadOnlyCollection<Guid> teamIds,
        RotaReadShape shape,
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

    // ==========================================================================
    // Shift tags
    // ==========================================================================

    Task<IReadOnlyList<ShiftTag>> GetTagsAsync(string? query = null, CancellationToken ct = default);

    /// <summary>
    /// Gets an existing tag by case-insensitive name or creates it.
    /// </summary>
    Task<ShiftTag> GetOrCreateTagAsync(string name, CancellationToken ct = default);

    // ==========================================================================
    // Volunteer tag preferences
    // ==========================================================================

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

    /// <summary>
    /// Loads <see cref="VolunteerEventProfile"/> rows for the supplied user ids
    /// in one query (read-only). Backs the bulk path on
    /// <see cref="Application.Services.Shifts.ShiftViewService.GetUsersAsync"/>.
    /// </summary>
    Task<IReadOnlyList<VolunteerEventProfile>> GetVolunteerEventProfilesByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task AddVolunteerEventProfileAsync(
        VolunteerEventProfile profile, CancellationToken ct = default);

    Task UpdateVolunteerEventProfileAsync(
        VolunteerEventProfile profile, CancellationToken ct = default);

    /// <summary>
    /// Deletes every <c>VolunteerEventProfile</c> row belonging to
    /// <paramref name="userId"/>. Returns the number of rows removed. Used by
    /// the account anonymization flow so the Shifts section owns
    /// <c>volunteer_event_profiles</c> writes (design-rules §2c).
    /// </summary>
    Task<int> DeleteVolunteerEventProfilesForUserAsync(
        Guid userId, CancellationToken ct = default);

    // ==========================================================================
    // Account-merge fold
    // ==========================================================================

    /// <summary>
    /// Account-merge fold: re-FK <c>VolunteerEventProfile</c> and
    /// <c>VolunteerTagPreference</c> rows from <paramref name="sourceUserId"/>
    /// to <paramref name="targetUserId"/> in a single
    /// <see cref="Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync(CancellationToken)"/>.
    /// Target wins on collision: if target already has a
    /// <c>VolunteerEventProfile</c>, source's profile row is removed;
    /// for tag preferences, any source row matching an existing target
    /// <c>(UserId, ShiftTagId)</c> is removed. Returns the total count of
    /// rows attributed to target across both tables after the move.
    /// </summary>
    Task<int> ReassignProfilesAndTagPrefsToUserAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Instant updatedAt,
        CancellationToken ct = default);
}

public enum EntityMutationMode
{
    Add,
    Update
}

[Flags]
public enum RotaReadShape
{
    None = 0,
    EventSettings = 1,
    Shifts = 2,
    ShiftSignups = 4,
    Tags = 8,
    ShiftsWithSignups = Shifts | ShiftSignups,
    View = EventSettings | Shifts | ShiftSignups | Tags
}

[Flags]
public enum ShiftReadShape
{
    None = 0,
    Rota = 1,
    EventSettings = 2,
    ShiftSignups = 4,
    Context = Rota | EventSettings | ShiftSignups
}

[Flags]
public enum ShiftEventQueryFlags
{
    None = 0,
    ExcludeAdminOnly = 1,
    ExcludeHiddenRotas = 2,
    IncludeSignups = 4,
    IncludeRotaTags = 8
}

public sealed record ShiftEventQuery(
    Guid EventSettingsId,
    IReadOnlyCollection<Guid>? TeamIds = null,
    int? MinDayOffset = null,
    int? MaxDayOffset = null,
    ShiftEventQueryFlags Flags = ShiftEventQueryFlags.None);
