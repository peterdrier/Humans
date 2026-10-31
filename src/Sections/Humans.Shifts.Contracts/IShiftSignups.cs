
using NodaTime;

namespace Humans.Shifts.Contracts;

/// <summary>
/// The signup verbs other sections call: profile and onboarding sign-ups, no-show history, and the cancel that
/// account anonymisation runs.
/// </summary>
public interface IShiftSignups
{
    /// <summary>
    /// Auto-confirms on Public rotas. Pass <see cref="ShiftSignupRequestFlags.Privileged"/> only when the caller has
    /// verified an admin or coordinator.
    /// </summary>
    Task<SignupResult> SignUpAsync(
        Guid userId,
        Guid shiftId,
        Guid? actorUserId = null,
        ShiftSignupRequestFlags flags = ShiftSignupRequestFlags.None);

    /// <summary>All-day shifts over a range, sharing a SignupBlockId for grouped bail.</summary>
    Task<SignupResult> SignUpRangeAsync(
        Guid userId,
        Guid rotaId,
        int startDayOffset,
        int endDayOffset,
        Guid? actorUserId = null,
        ShiftSignupRequestFlags flags = ShiftSignupRequestFlags.None);

    /// <summary>No-show entries with review timestamps, without display ordering.</summary>
    Task<IReadOnlyList<NoShowHistoryEntry>> GetNoShowHistoryAsync(Guid userId, CancellationToken ct = default);

    /// <summary>One atomic save; returns each cancelled signup so the caller can audit them one by one.</summary>
    Task<IReadOnlyList<(Guid SignupId, Guid ShiftId)>> CancelActiveSignupsForUserAsync(
        Guid userId, string reason, CancellationToken ct = default);
}

/// <summary>
/// The verbs <c>Humans.Development</c>'s seeder adds for a realistic status spread and its reset path.
/// </summary>
public interface IShiftSignupSeeding : IShiftSignups
{
    Task<SignupResult> VoluntellAsync(Guid userId, Guid shiftId, Guid enrollerUserId);

    Task<SignupResult> BailAsync(Guid signupId, Guid actorUserId, string? reason);

    Task<SignupResult> RefuseAsync(Guid signupId, Guid reviewerUserId, string? reason);

    /// <summary>Requires the current user to hold the full Admin role.</summary>
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

/// <summary>For a block signup <see cref="SignupId"/> is the last row created.</summary>
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

public static class ShiftSignupHelper
{
    /// <summary>The single source of truth for which statuses count as active.</summary>
    public static (HashSet<Guid> ShiftIds, Dictionary<Guid, SignupStatus> Statuses) ResolveActiveStatuses(
        IReadOnlyList<ShiftSignupSummary> signups)
    {
        var active = signups.Where(s => s.IsActive).ToList();

        var shiftIds = active.Select(s => s.ShiftId).ToHashSet();
        var statuses = active.ToDictionary(s => s.ShiftId, s => s.Status);

        return (shiftIds, statuses);
    }
}
