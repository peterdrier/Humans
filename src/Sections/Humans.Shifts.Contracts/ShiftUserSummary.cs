
using NodaTime;

namespace Humans.Shifts.Contracts;

/// <summary>
/// The flattened, entity-free shape of the section's cached per-user rows. Profile, availability and build status
/// are absent: nothing outside read them; the profile is on <see cref="IShiftVolunteerProfiles"/>.
/// </summary>
public sealed record ShiftUserSummary(
    Guid UserId,
    IReadOnlyList<ShiftTagPreferenceInfo> TagPreferences,
    IReadOnlyList<ShiftSignupSummary> Signups)
{
    /// <summary>Pending or Confirmed in the active event; the other statuses are no longer commitments.</summary>
    public bool HasShift => Signups.Any(s => s.IsActive);

    public int ActiveSignupCount => Signups.Count(s => s.IsActive);

    /// <summary>Active signups only; the period is resolved inside the section against the event's dates.</summary>
    public bool HasShiftInPeriod(ShiftPeriod period) =>
        Signups.Any(s => s.IsActive && s.Period == period);

    /// <summary>For unknown ids or no active event.</summary>
    public static ShiftUserSummary Empty(Guid userId) => new(userId, [], []);
}

public sealed record ShiftTagPreferenceInfo(Guid ShiftTagId, string Name);

/// <summary>
/// <c>SignupBlockId</c> is shared by every row of a multi-day block. <c>RotaName</c> is empty when the rota row is
/// missing. For an all-day shift the window is the all-day window.
/// </summary>
public sealed record ShiftSignupSummary(
    Guid Id,
    Guid ShiftId,
    Guid? SignupBlockId,
    SignupStatus Status,
    Guid RotaId,
    string RotaName,
    string? RotaDescription,
    string? RotaPracticalInfo,
    Guid TeamId,
    Guid EventSettingsId,
    LocalDate Date,
    ShiftPeriod Period,
    bool IsAllDay,
    LocalTime WindowStart,
    LocalTime WindowEnd,
    double DurationHours,
    string? ShiftDescription,
    Instant AbsoluteStart,
    Instant AbsoluteEnd)
{
    public bool IsActive => Status is SignupStatus.Pending or SignupStatus.Confirmed;
}
