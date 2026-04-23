using Humans.Domain.Entities;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Shifts section's <c>shift_signups</c> table plus the
/// narrow within-section cross-service reads ShiftSignupService currently
/// performs on <c>rotas</c>, <c>shifts</c>, <c>volunteer_event_profiles</c>,
/// <c>general_availabilities</c>, and <c>volunteer_tag_preferences</c>.
/// </summary>
/// <remarks>
/// <para>
/// Only non-test file that may write to or query
/// <c>DbContext.ShiftSignups</c> from the <c>ShiftSignupService</c> migration
/// onward (issue #541 sub-task b).
/// </para>
/// <para>
/// Within-section cross-service reads (rotas, shifts, volunteer_event_profiles,
/// general_availability, volunteer_tag_preferences) live here for now — they
/// are acceptable per <c>docs/sections/Shifts.md</c> "within-section
/// cross-service direct DbContext reads" provisional rule until the other
/// Shifts services land behind their own repositories (#541a, #541c surface
/// expansion). When those migrations land, these reads should move to
/// <c>IShiftManagementRepository</c> / <c>IGeneralAvailabilityRepository</c>
/// / etc.
/// </para>
/// <para>
/// Read methods are <c>AsNoTracking</c> by default; methods named
/// <c>ForMutation</c> return tracking-enabled entities whose changes are
/// persisted by a matching <c>SaveChangesAsync</c> on the same repository
/// (same Scoped <c>HumansDbContext</c> instance).
/// </para>
/// </remarks>
public interface IShiftSignupRepository
{
    // ============================================================
    // Reads — ShiftSignup (per-user / per-shift / per-block)
    // ============================================================

    /// <summary>
    /// Returns <c>true</c> if the user has a Pending or Confirmed signup for
    /// the given shift. Read-only.
    /// </summary>
    Task<bool> HasActiveSignupAsync(Guid userId, Guid shiftId, CancellationToken ct = default);

    /// <summary>
    /// Returns every <see cref="ShiftSignup"/> the user owns, optionally
    /// filtered to a single event. Includes <c>Shift.Rota.Team</c> and
    /// <c>Shift.Rota.EventSettings</c> for display. Ordered by
    /// <c>Shift.DayOffset</c>, then <c>Shift.StartTime</c>. Read-only.
    /// </summary>
    Task<IReadOnlyList<ShiftSignup>> GetByUserAsync(
        Guid userId, Guid? eventSettingsId = null, CancellationToken ct = default);

    /// <summary>
    /// Returns active (Pending or Confirmed) signups for the user, including
    /// <c>Shift.Rota.EventSettings</c> and <c>Shift.Rota.Team</c> for overlap
    /// checks. Read-only.
    /// </summary>
    Task<IReadOnlyList<ShiftSignup>> GetActiveSignupsForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Loads a single signup by id with <c>Shift.Rota.Team</c>. Read-only.
    /// </summary>
    Task<ShiftSignup?> GetByIdAsync(Guid signupId, CancellationToken ct = default);

    /// <summary>
    /// Loads a signup by id with full shift / rota / event-settings / team
    /// context and the shift's sibling signups (for capacity checks).
    /// Tracking-enabled.
    /// </summary>
    Task<ShiftSignup?> GetByIdForMutationAsync(Guid signupId, CancellationToken ct = default);

    /// <summary>
    /// Loads every Pending signup in a block (or both Pending and Confirmed
    /// when <paramref name="includeConfirmed"/> is true). Includes
    /// <c>Shift.Rota.EventSettings</c>, <c>Shift.Rota.Team</c>, and the
    /// shift's sibling signups (for capacity checks in range-approve).
    /// Tracking-enabled.
    /// </summary>
    Task<List<ShiftSignup>> GetBlockForMutationAsync(
        Guid signupBlockId, bool includeConfirmed, CancellationToken ct = default);

    /// <summary>
    /// Loads the first signup in a block with <c>Shift.Rota</c>. Read-only,
    /// used for block-ownership lookups.
    /// </summary>
    Task<ShiftSignup?> GetByBlockIdFirstAsync(Guid signupBlockId, CancellationToken ct = default);

    /// <summary>
    /// Returns all signups for a shift ordered by <c>CreatedAt</c>, including
    /// the signup's <c>User</c> (display name) and <c>Shift.Rota</c>.
    /// Read-only.
    /// </summary>
    /// <remarks>
    /// Cross-domain <c>.Include(d =&gt; d.User)</c> is deliberately preserved
    /// here pending the Shifts-wide User-nav strip (tracked in §15i). The
    /// Shift-admin view reads <c>signup.User.DisplayName</c>; moving that to
    /// an in-memory stitch is out of scope for this migration PR.
    /// </remarks>
    Task<IReadOnlyList<ShiftSignup>> GetByShiftAsync(Guid shiftId, CancellationToken ct = default);

    /// <summary>
    /// Returns all no-show signups for a user with full shift / rota / event /
    /// team context plus <c>ReviewedByUser</c>. Ordered by <c>ReviewedAt</c>
    /// descending. Read-only.
    /// </summary>
    /// <remarks>
    /// Cross-domain <c>.Include(s =&gt; s.ReviewedByUser)</c> is deliberately
    /// preserved here pending the Shifts-wide User-nav strip. Profile view
    /// reads <c>s.ReviewedByUser.DisplayName</c>.
    /// </remarks>
    Task<IReadOnlyList<ShiftSignup>> GetNoShowHistoryAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the subset of <paramref name="shiftIds"/> for which the user
    /// already has a Pending or Confirmed signup. Read-only.
    /// </summary>
    Task<HashSet<Guid>> GetActiveShiftIdsForUserAsync(
        Guid userId, IReadOnlyCollection<Guid> shiftIds, CancellationToken ct = default);

    /// <summary>
    /// Returns the count of Confirmed signups per shift for the given shift
    /// ids. Shifts with zero confirmed are absent from the dictionary.
    /// Read-only.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetConfirmedCountsByShiftAsync(
        IReadOnlyCollection<Guid> shiftIds, CancellationToken ct = default);

    /// <summary>
    /// Returns the count of distinct users with a Confirmed early-entry
    /// signup in the given event on the given day offset. Used for the EE
    /// cap check. Read-only.
    /// </summary>
    Task<int> GetDistinctEeUsersOnDayAsync(
        Guid eventSettingsId, int dayOffset, CancellationToken ct = default);

    /// <summary>
    /// Returns every signup row owned by the user for the GDPR export, with
    /// <c>Shift.Rota.EventSettings</c> included (for event name and date
    /// resolution). No cross-domain User includes. Ordered by <c>CreatedAt</c>
    /// descending. Read-only.
    /// </summary>
    Task<IReadOnlyList<ShiftSignup>> GetForGdprExportAsync(Guid userId, CancellationToken ct = default);

    // ============================================================
    // Reads — within-section cross-service (pending #541a / #541c)
    // ============================================================

    /// <summary>
    /// Loads a single shift with full rota / event-settings / team context
    /// and its sibling signups for capacity checks. Read-only.
    /// </summary>
    /// <remarks>
    /// Within-section cross-service read: <c>shifts</c> and <c>rotas</c> are
    /// owned by <c>ShiftManagementService</c> (#541a). Move to
    /// <c>IShiftManagementRepository</c> when #541a lands.
    /// </remarks>
    Task<Shift?> GetShiftWithContextAsync(Guid shiftId, CancellationToken ct = default);

    /// <summary>
    /// Loads a rota with <c>EventSettings</c> and all <c>Shifts</c> for range
    /// operations. Read-only.
    /// </summary>
    /// <remarks>
    /// Within-section cross-service read: <c>rotas</c> are owned by
    /// <c>ShiftManagementService</c> (#541a). Move to
    /// <c>IShiftManagementRepository</c> when #541a lands.
    /// </remarks>
    Task<Rota?> GetRotaWithShiftsAsync(Guid rotaId, CancellationToken ct = default);

    /// <summary>
    /// GDPR contributor read: all <c>VolunteerEventProfile</c> rows for a
    /// user. Within-section cross-service read; move to its own repository
    /// in a follow-up.
    /// </summary>
    Task<IReadOnlyList<VolunteerEventProfile>> GetVolunteerEventProfilesForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// GDPR contributor read: all <c>GeneralAvailability</c> rows for a user
    /// with <c>EventSettings</c> included. Within-section cross-service read,
    /// will move through <c>IGeneralAvailabilityRepository</c> (#541c) when
    /// that surface expands.
    /// </summary>
    Task<IReadOnlyList<GeneralAvailability>> GetGeneralAvailabilityForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// GDPR contributor read: all <c>VolunteerTagPreference</c> rows for a
    /// user with <c>ShiftTag</c> included. Within-section cross-service read;
    /// move to its own repository in a follow-up.
    /// </summary>
    Task<IReadOnlyList<VolunteerTagPreference>> GetVolunteerTagPreferencesForUserAsync(
        Guid userId, CancellationToken ct = default);

    // ============================================================
    // Writes — ShiftSignup
    // ============================================================

    /// <summary>
    /// Adds a new <see cref="ShiftSignup"/> to the context without committing.
    /// Call <see cref="SaveChangesAsync"/> afterwards — typically the caller
    /// has already issued a mutation-load and wants a single transaction for
    /// the add + state change.
    /// </summary>
    void Add(ShiftSignup signup);

    /// <summary>
    /// Adds many <see cref="ShiftSignup"/> rows to the context without
    /// committing.
    /// </summary>
    void AddRange(IEnumerable<ShiftSignup> signups);

    /// <summary>
    /// Persists all pending mutations on the repository's underlying context.
    /// Entities loaded via <see cref="GetByIdForMutationAsync"/> or
    /// <see cref="GetBlockForMutationAsync"/> and mutated by the caller are
    /// persisted here, along with any <see cref="Add"/>/<see cref="AddRange"/>
    /// calls made in the same request.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Cancels every <c>Confirmed</c> or <c>Pending</c> signup belonging to
    /// <paramref name="userId"/>, stamping the supplied <paramref name="reason"/>
    /// and persisting in one <c>SaveChangesAsync</c> call. Returns the id and
    /// shift id of each signup that was cancelled so the caller can emit
    /// per-signup audit entries.
    /// </summary>
    Task<IReadOnlyList<(Guid SignupId, Guid ShiftId)>> CancelActiveSignupsForUserAsync(
        Guid userId, string reason, CancellationToken ct = default);
}
