using Humans.Shifts.Domain;
using Humans.Shifts.Contracts;

using NodaTime;
using Humans.Base.Interfaces;

namespace Humans.Shifts.Services;

/// <summary>
/// Manages the shift signup state machine with invariant enforcement.
/// </summary>
/// <remarks>
/// The section's own interface. The members something outside the section
/// calls live on <see cref="IShiftSignups"/> and <see cref="IShiftSignupSeeding"/>
/// in <c>Humans.Shifts.Contracts</c>; this inherits them, so the section's own
/// injection sites are unchanged. Everything declared here — the review
/// surface, the block-range operations, the orphan scan, the team probe, the
/// audience read and the day toggle — has no external caller.
/// </remarks>
internal interface IShiftSignupService : IShiftSignupSeeding, IApplicationService
{
    /// <summary>
    /// Approves a pending signup. Re-validates invariants.
    /// </summary>
    Task<SignupResult> ApproveAsync(Guid signupId, Guid reviewerUserId);

    /// <summary>
    /// Creates confirmed signups across a date range on behalf of a volunteer (batch voluntell).
    /// Skips shifts where the user already has an active signup.
    /// All signups share a SignupBlockId for grouped bail.
    /// </summary>
    Task<SignupResult> VoluntellRangeAsync(Guid userId, Guid rotaId, int startDayOffset, int endDayOffset, Guid enrollerUserId);

    /// <summary>
    /// Marks a confirmed signup as no-show (post-shift only).
    /// </summary>
    Task<SignupResult> MarkNoShowAsync(Guid signupId, Guid reviewerUserId);

    /// <summary>
    /// Removes a confirmed signup (coordinator/admin unassignment).
    /// </summary>
    Task<SignupResult> RemoveSignupAsync(Guid signupId, Guid removedByUserId, string? reason);

    /// <summary>
    /// Approves all pending signups sharing a SignupBlockId.
    /// </summary>
    Task<SignupResult> ApproveRangeAsync(Guid signupBlockId, Guid reviewerUserId);

    /// <summary>
    /// Refuses all pending signups sharing a SignupBlockId.
    /// </summary>
    Task<SignupResult> RefuseRangeAsync(Guid signupBlockId, Guid reviewerUserId, string? reason);

    /// <summary>
    /// Bails all signups sharing a SignupBlockId.
    /// </summary>
    Task BailRangeAsync(Guid signupBlockId, Guid actorUserId, string? reason = null);

    /// <summary>
    /// Gets all signups for a user, optionally filtered by event.
    /// </summary>
    Task<IReadOnlyList<ShiftSignup>> GetByUserAsync(Guid userId, Guid? eventSettingsId = null);

    /// <summary>
    /// Gets signup team ownership data for admin authorization checks.
    /// </summary>
    Task<ShiftSignupTeamProbe?> GetTeamProbeAsync(Guid id, ShiftSignupTeamProbeScope scope);

    /// <summary>
    /// Returns every <see cref="ShiftSignup"/> in the system, with
    /// <c>Shift.Rota.EventSettings</c> included, for use by the
    /// orphan-signup reconciliation screen. Admin-only diagnostic.
    /// </summary>
    Task<IReadOnlyList<OrphanSignupSnapshot>> GetAllForOrphanScanAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns user-ids with at least one ShiftSignup for the given event whose
    /// Status is Pending or Confirmed. Used by audience computations to identify
    /// "users who have a shift". Refused/Bailed/Cancelled/NoShow signups do not count.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetActiveCommittedUserIdsForEventAsync(
        Guid eventSettingsId, CancellationToken ct = default);

    /// <summary>
    /// Self-service day-row toggle for the current user: bails an existing active
    /// signup for <paramref name="shiftId"/>, or signs up if none exists (auto-confirm
    /// per <paramref name="privileged"/>, mirroring <see cref="IShiftSignups.SignUpAsync"/>'s
    /// Privileged flag). Short-circuits with <see cref="ToggleDaySignupOutcome.NeedsDietaryFirst"/>
    /// instead of signing up when the shift qualifies for a cantina meal and
    /// <paramref name="hasDietaryPreference"/> is false — the caller owns the redirect.
    /// <see cref="ToggleDaySignupOutcome.CanViewRestricted"/> folds
    /// <paramref name="privileged"/> together with dept-coordinator status, matching the
    /// browse page's broader privilege check, for restricted-shift row rendering.
    /// </summary>
    Task<ToggleDaySignupOutcome> ToggleDayAsync(
        Guid userId,
        Guid shiftId,
        Guid eventSettingsId,
        bool privileged,
        bool hasDietaryPreference,
        CancellationToken ct = default);
}

/// <summary>
/// Outcome of <see cref="IShiftSignupService.ToggleDayAsync"/>.
/// </summary>
internal sealed record ToggleDaySignupOutcome(
    bool NeedsDietaryFirst,
    SignupResult? Result,
    bool SignedUp,
    bool CanViewRestricted,
    IReadOnlyList<ShiftSignup> SignupsAfter)
{
    internal static ToggleDaySignupOutcome DietaryRequired() => new(true, null, false, false, []);
}

internal sealed record ShiftSignupTeamProbe(
    Guid Id,
    Guid ShiftId,
    Guid TeamId);

internal enum ShiftSignupTeamProbeScope
{
    Signup,
    SignupBlock
}

internal sealed record OrphanSignupSnapshot(
    Guid Id,
    Guid UserId,
    string RotaName,
    LocalDate ShiftDate,
    SignupStatus Status,
    Instant CreatedAt,
    Guid? ReviewedByUserId,
    Guid? EnrolledByUserId,
    Guid? SignupBlockId);
