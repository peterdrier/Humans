
using NodaTime;

namespace Humans.Shifts.Contracts;

/// <summary>
/// The part of the shift-signup state machine something outside the section
/// calls: Shell's profile page and the onboarding widget sign a volunteer up
/// (single shift or a Build/Strike range) and render their no-show history, and
/// the account-anonymisation flow cancels every active signup a departing human
/// holds.
/// </summary>
/// <remarks>
/// Carved from the call sites (Notifications' rule). The rest of the internal
/// <c>IShiftSignupService</c> — the approve/refuse/no-show/remove review
/// surface, the block-range operations, the orphan scan, the team probe, the
/// audience read and the day toggle — has no caller outside the section and
/// stays internal.
/// </remarks>
public interface IShiftSignups
{
    /// <summary>
    /// Creates a signup for a user on a shift. Auto-confirms for Public policy.
    /// Use <see cref="ShiftSignupRequestFlags.Privileged"/> when the caller has
    /// already verified the user is an admin or coordinator.
    /// </summary>
    Task<SignupResult> SignUpAsync(
        Guid userId,
        Guid shiftId,
        Guid? actorUserId = null,
        ShiftSignupRequestFlags flags = ShiftSignupRequestFlags.None);

    /// <summary>
    /// Creates signups for a date range of all-day shifts (build/strike).
    /// All signups share a SignupBlockId for grouped bail.
    /// </summary>
    Task<SignupResult> SignUpRangeAsync(
        Guid userId,
        Guid rotaId,
        int startDayOffset,
        int endDayOffset,
        Guid? actorUserId = null,
        ShiftSignupRequestFlags flags = ShiftSignupRequestFlags.None);

    /// <summary>
    /// Gets all no-show signups for a user, with shift/team context and reviewer info.
    /// </summary>
    Task<IReadOnlyList<NoShowHistoryEntry>> GetNoShowHistoryAsync(Guid userId);

    /// <summary>
    /// Cancels every Confirmed or Pending signup owned by
    /// <paramref name="userId"/> with the supplied <paramref name="reason"/>,
    /// in one atomic save. Returns the id + shift id of each signup that was
    /// cancelled so callers (account deletion job) can emit per-signup audit
    /// entries. Used by the account anonymization flow so the job does not
    /// write to <c>shift_signups</c> directly (design-rules §2c).
    /// </summary>
    Task<IReadOnlyList<(Guid SignupId, Guid ShiftId)>> CancelActiveSignupsForUserAsync(
        Guid userId, string reason, CancellationToken ct = default);
}

/// <summary>
/// The signup verbs <c>Humans.Development</c>'s dashboard seeder drives on top
/// of <see cref="IShiftSignups"/>: voluntell, bail and refuse to give the demo
/// data a realistic status spread, and a bulk delete for the reset path.
/// </summary>
/// <remarks>
/// Same call as <see cref="IShiftSeeding"/> — the seeder builds a
/// multi-section fixture, so the verbs come to the leaf rather than the
/// seeding going into the section (Teams' rule). Inherits
/// <see cref="IShiftSignups"/> so the seeder injects one interface.
/// </remarks>
public interface IShiftSignupSeeding : IShiftSignups
{
    /// <summary>
    /// Creates a confirmed signup on behalf of a volunteer (voluntell).
    /// </summary>
    Task<SignupResult> VoluntellAsync(Guid userId, Guid shiftId, Guid enrollerUserId);

    /// <summary>
    /// Bails from a confirmed or pending signup.
    /// </summary>
    Task<SignupResult> BailAsync(Guid signupId, Guid actorUserId, string? reason);

    /// <summary>
    /// Refuses a pending signup.
    /// </summary>
    Task<SignupResult> RefuseAsync(Guid signupId, Guid reviewerUserId, string? reason);

    /// <summary>
    /// Deletes every shift signup owned by the supplied users. Requires the
    /// current authenticated user to hold the full Admin role.
    /// </summary>
    Task<int> DeleteAllForUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);
}

[Flags]
public enum ShiftSignupRequestFlags
{
    None = 0,
    Privileged = 1,
    SkipConflicts = 2
}

public record NoShowHistoryEntry(
    string ShiftLabel,
    Guid TeamId,
    Instant ShiftStart,
    string TimeZoneId,
    Guid? ReviewedByUserId,
    Instant? ReviewedAt);

/// <summary>
/// Result of a signup operation.
/// </summary>
/// <remarks>
/// <see cref="SignupId"/> used to be the <c>ShiftSignup</c> row itself. Nothing
/// outside the section read anything but its id — the dev seeder marks the
/// created signup as reviewed — so the boundary carries the id, matching
/// <see cref="ShiftMutationResult.ShiftId"/> (nobodies-collective/Humans#866).
/// For a block signup it is the last row created, which is what the entity
/// return was.
/// </remarks>
public record SignupResult
{
    public bool Success { get; init; }
    public string? Warning { get; init; }
    public string? Error { get; init; }
    public Guid? SignupId { get; init; }

    public static SignupResult Ok(Guid signupId, string? warning = null) =>
        new() { Success = true, SignupId = signupId, Warning = warning };

    public static SignupResult Fail(string error) =>
        new() { Success = false, Error = error };
}

/// <summary>
/// Helper for resolving active signup statuses from an already-loaded list of signups.
/// Use this when the caller already has signups from
/// <see cref="ShiftUserSummary.Signups"/> and needs the filtered result without
/// an additional DB round-trip.
/// </summary>
public static class ShiftSignupHelper
{
    /// <summary>
    /// Filters signups to active statuses (Confirmed or Pending) and returns shift IDs and status dictionary.
    /// Single source of truth for "active signup statuses" filtering logic.
    /// </summary>
    public static (HashSet<Guid> ShiftIds, Dictionary<Guid, SignupStatus> Statuses) ResolveActiveStatuses(
        IReadOnlyList<ShiftSignupSummary> signups)
    {
        var active = signups.Where(s => s.IsActive).ToList();

        var shiftIds = active.Select(s => s.ShiftId).ToHashSet();
        var statuses = active.ToDictionary(s => s.ShiftId, s => s.Status);

        return (shiftIds, statuses);
    }
}
