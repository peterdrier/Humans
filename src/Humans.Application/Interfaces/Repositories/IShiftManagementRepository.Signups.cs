using Humans.Domain.Entities;
using Humans.Application.Interfaces.Shifts;
using NodaTime;
namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Signup-focused members of the Shifts repository contract.
/// </summary>
/// <remarks>
/// <para>
/// <c>ShiftRepository</c> is the single concrete persistence adapter for Shifts.
/// These members stay beside the signup state-machine operations even though
/// they are declared on the same repository interface as rota/event management.
/// </para>
/// <para>
/// Read methods are <c>AsNoTracking</c> by default; methods named
/// <c>ForMutation</c> return tracking-enabled entities whose changes are
/// persisted by a matching <c>SaveChangesAsync</c> on the same repository
/// (same Scoped <c>HumansDbContext</c> instance).
/// </para>
/// </remarks>
public partial interface IShiftManagementRepository
{
    // ============================================================
    // Reads — ShiftSignup (per-user / per-shift / per-block)
    // ============================================================

    /// <summary>
    /// Returns <see cref="ShiftSignup"/> rows for the supplied users,
    /// optionally filtered to a single event. Includes
    /// <c>Shift.Rota.EventSettings</c>; team display names are resolved via
    /// <c>ITeamService.GetTeamsAsync</c>. Read-only.
    /// </summary>
    Task<IReadOnlyList<ShiftSignup>> GetForUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        Guid? eventSettingsId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Loads signup team ownership data by signup id or signup block id. Read-only.
    /// </summary>
    Task<ShiftSignup?> GetTeamProbeAsync(
        Guid id, ShiftSignupTeamProbeScope scope, CancellationToken ct = default);

    /// <summary>
    /// Loads a signup by id with full shift / rota / event-settings context
    /// and the shift's sibling signups (for capacity checks). Tracking-enabled.
    /// </summary>
    Task<ShiftSignup?> GetByIdForMutationAsync(Guid signupId, CancellationToken ct = default);

    /// <summary>
    /// Loads every Pending signup in a block, or both Pending and Confirmed
    /// when requested by <paramref name="scope"/>. Includes
    /// <c>Shift.Rota.EventSettings</c> and the shift's sibling signups (for
    /// capacity checks in range-approve). Tracking-enabled.
    /// </summary>
    Task<List<ShiftSignup>> GetBlockForMutationAsync(
        Guid signupBlockId,
        ShiftSignupBlockMutationScope scope,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the subset of <paramref name="shiftIds"/> for which the user
    /// already has a Pending or Confirmed signup. Read-only.
    /// </summary>
    Task<HashSet<Guid>> GetActiveShiftIdsForUserAsync(
        Guid userId, IReadOnlyCollection<Guid> shiftIds, CancellationToken ct = default);

    /// <summary>
    /// Returns distinct users with shift signups in the given event on the
    /// given day offset, filtered by signup status. Read-only.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUserIdsForDayAsync(
        Guid eventSettingsId,
        int dayOffset,
        ShiftDayUserStatusScope statusScope,
        CancellationToken ct = default);

    // ============================================================
    // Reads - signup-adjacent Shifts data
    // ============================================================

    /// <summary>
    /// Loads <see cref="VolunteerTagPreference"/> rows for the supplied user
    /// ids in one query, with <c>ShiftTag</c> included (read-only). Backs the
    /// bulk path on
    /// <see cref="Application.Services.Shifts.ShiftViewService.GetUsersAsync"/>.
    /// </summary>
    Task<IReadOnlyList<VolunteerTagPreference>> GetVolunteerTagPreferencesForUsersAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    // ============================================================
    // Writes — ShiftSignup
    // ============================================================

    /// <summary>
    /// Adds many <see cref="ShiftSignup"/> rows to the context without
    /// committing.
    /// </summary>
    void AddRange(IEnumerable<ShiftSignup> signups);

    /// <summary>
    /// Persists all pending mutations on the repository's underlying context.
    /// Entities loaded via <see cref="GetByIdForMutationAsync"/> or
    /// <see cref="GetBlockForMutationAsync"/> and mutated by the caller are
    /// persisted here, along with any <see cref="AddRange"/> calls made in the
    /// same request.
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

    /// <summary>
    /// Deletes every signup row owned by the supplied users. Returns deleted row count.
    /// </summary>
    Task<int> DeleteAllForUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);

    /// <summary>
    /// Account-merge fold: re-FKs <c>shift_signups</c> rows from
    /// <paramref name="sourceUserId"/> to <paramref name="targetUserId"/>.
    /// Plain re-FK with one defensive guard — when source and target both
    /// have a row for the same <c>ShiftId</c>, the source row is dropped so
    /// the slot is not duplicated under the merged user. (No DB-level
    /// unique constraint on <c>(UserId, ShiftId)</c>; the no-double-signup
    /// invariant is enforced at the service layer.) Stamps <c>UpdatedAt</c>
    /// with <paramref name="updatedAt"/> on every re-FK'd row. Single
    /// <c>SaveChanges</c>. Returns the number of source rows touched
    /// (re-FK'd plus dropped-as-duplicate) so the caller can audit the merge.
    /// </summary>
    Task<int> ReassignToUserAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Instant updatedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Reads every <see cref="ShiftSignup"/> in the system with
    /// <c>Shift.Rota.EventSettings</c> included, used by the orphan-signup
    /// reconciliation screen. Read-only.
    /// </summary>
    Task<IReadOnlyList<ShiftSignup>> GetAllForOrphanScanAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns user-ids with at least one Pending or Confirmed signup for the
    /// given event. Read-only. Used by Mailer audience computations.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetActiveCommittedUserIdsForEventAsync(
        Guid eventSettingsId, CancellationToken ct = default);
}

public enum ShiftSignupBlockMutationScope
{
    PendingOnly,
    PendingAndConfirmed
}

public enum ShiftDayUserStatusScope
{
    ConfirmedOnly,
    PendingOrConfirmed
}
