using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces;

/// <summary>
/// Manages the shift signup state machine with invariant enforcement.
/// </summary>
public interface IShiftSignupService
{
    /// <summary>
    /// Creates a signup for a user on a shift. Auto-confirms for Public policy.
    /// Pass isPrivileged=true when the controller has already verified the user is Admin/coordinator.
    /// </summary>
    Task<SignupResult> SignUpAsync(Guid userId, Guid shiftId, Guid? actorUserId = null, bool isPrivileged = false);

    /// <summary>
    /// Approves a pending signup. Re-validates invariants.
    /// </summary>
    Task<SignupResult> ApproveAsync(Guid signupId, Guid reviewerUserId);

    /// <summary>
    /// Refuses a pending signup.
    /// </summary>
    Task<SignupResult> RefuseAsync(Guid signupId, Guid reviewerUserId, string? reason);

    /// <summary>
    /// Bails from a confirmed or pending signup.
    /// </summary>
    Task<SignupResult> BailAsync(Guid signupId, Guid actorUserId, string? reason);

    /// <summary>
    /// Creates a confirmed signup on behalf of a volunteer (voluntell).
    /// </summary>
    Task<SignupResult> VoluntellAsync(Guid userId, Guid shiftId, Guid enrollerUserId);

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
    /// Creates signups for a date range of all-day shifts (build/strike).
    /// All signups share a SignupBlockId for grouped bail.
    /// </summary>
    Task<SignupResult> SignUpRangeAsync(Guid userId, Guid rotaId, int startDayOffset, int endDayOffset, Guid? actorUserId = null, bool isPrivileged = false);

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
    /// Gets a signup by primary key with Shift.Rota.Team included.
    /// </summary>
    Task<ShiftSignup?> GetByIdAsync(Guid signupId);

    /// <summary>
    /// Gets the first signup in a block with Shift.Rota included (for team ownership checks).
    /// </summary>
    Task<ShiftSignup?> GetByBlockIdFirstAsync(Guid signupBlockId);

    /// <summary>
    /// Gets all signups for a shift.
    /// </summary>
    Task<IReadOnlyList<ShiftSignup>> GetByShiftAsync(Guid shiftId);

    /// <summary>
    /// Gets all no-show signups for a user, with shift/team context and reviewer info.
    /// </summary>
    Task<IReadOnlyList<ShiftSignup>> GetNoShowHistoryAsync(Guid userId);

    /// <summary>
    /// Gets active signup statuses (Confirmed or Pending) for a user in a specific event.
    /// Returns a tuple of (shiftIds the user is signed up for, shiftId → status dictionary).
    /// This is the single source of truth for "which shifts has this user actively signed up for?"
    /// </summary>
    Task<(HashSet<Guid> ShiftIds, Dictionary<Guid, SignupStatus> Statuses)> GetActiveSignupStatusesAsync(
        Guid userId, Guid eventSettingsId);
}

/// <summary>
/// Helper for resolving active signup statuses from an already-loaded list of signups.
/// Use this when the caller already has signups from GetByUserAsync and needs the filtered result
/// without an additional DB round-trip.
/// </summary>
public static class ShiftSignupHelper
{
    /// <summary>
    /// Filters signups to active statuses (Confirmed or Pending) and returns shift IDs and status dictionary.
    /// Single source of truth for "active signup statuses" filtering logic.
    /// </summary>
    public static (HashSet<Guid> ShiftIds, Dictionary<Guid, SignupStatus> Statuses) ResolveActiveStatuses(
        IReadOnlyList<ShiftSignup> signups)
    {
        var active = signups
            .Where(s => s.Status is SignupStatus.Confirmed or SignupStatus.Pending)
            .ToList();

        var shiftIds = active.Select(s => s.ShiftId).ToHashSet();
        var statuses = active.ToDictionary(s => s.ShiftId, s => s.Status);

        return (shiftIds, statuses);
    }
}

/// <summary>
/// Result of a signup operation.
/// </summary>
public record SignupResult
{
    public bool Success { get; init; }
    public string? Warning { get; init; }
    public string? Error { get; init; }
    public ShiftSignup? Signup { get; init; }

    public static SignupResult Ok(ShiftSignup signup, string? warning = null) =>
        new() { Success = true, Signup = signup, Warning = warning };

    public static SignupResult Fail(string error) =>
        new() { Success = false, Error = error };
}
