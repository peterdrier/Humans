
using NodaTime;

namespace Humans.Shifts.Contracts;

/// <summary>
/// Flat per-user projection of the Shifts section's rows for the active
/// event, returned by <see cref="IShiftView"/>.
/// </summary>
/// <remarks>
/// The section caches an entity-bearing bundle per user (its internal
/// <c>ShiftUserView</c>); this is the flattened shape of it that crosses the
/// section boundary, so <c>Shift</c>, <c>ShiftSignup</c>, <c>Rota</c>,
/// <c>VolunteerEventProfile</c>, <c>GeneralAvailability</c> and
/// <c>VolunteerBuildStatus</c> stay inside the section
/// (nobodies-collective/Humans#866, G5).
///
/// <para>
/// The volunteer profile, availability and build-status rows the internal
/// bundle carries are deliberately absent: no consumer outside the section
/// ever read them off the view. The profile is read through
/// <see cref="IShiftVolunteerProfiles"/> instead.
/// </para>
/// </remarks>
public sealed record ShiftUserSummary(
    Guid UserId,
    IReadOnlyList<ShiftTagPreferenceInfo> TagPreferences,
    IReadOnlyList<ShiftSignupSummary> Signups)
{
    /// <summary>
    /// True when the user has at least one signup in an active state
    /// (<see cref="SignupStatus.Pending"/> or <see cref="SignupStatus.Confirmed"/>)
    /// in the active event. Refused / Bailed / Cancelled / NoShow signups
    /// don't count — they're no longer commitments. Mirrors the convention
    /// used by <c>ShiftRepository</c>, <c>ShiftManagementService</c>,
    /// and the agent snapshot.
    /// </summary>
    public bool HasShift => Signups.Any(s => s.IsActive);

    /// <summary>
    /// Number of active-state signups (<see cref="SignupStatus.Pending"/> or
    /// <see cref="SignupStatus.Confirmed"/>) the user holds in the active event.
    /// Refused / Bailed / Cancelled / NoShow signups don't count. Same
    /// "active commitment" rule as <see cref="HasShift"/>, expressed as a count.
    /// </summary>
    public int ActiveSignupCount => Signups.Count(s => s.IsActive);

    /// <summary>
    /// True when the user has at least one active signup (Pending/Confirmed) on
    /// a shift classified into the given <paramref name="period"/>. Each shift's
    /// period is derived inside the section from the shift's day offset against
    /// the event's gate/strike dates (Build = before gates open, Event = during,
    /// Strike = after).
    /// </summary>
    public bool HasShiftInPeriod(ShiftPeriod period) =>
        Signups.Any(s => s.IsActive && s.Period == period);

    /// <summary>
    /// Empty summary returned for unknown ids / no active event.
    /// </summary>
    public static ShiftUserSummary Empty(Guid userId) => new(userId, [], []);
}

/// <summary>
/// One of the user's shift-tag preferences, with the tag's display name
/// already stitched in.
/// </summary>
public sealed record ShiftTagPreferenceInfo(Guid ShiftTagId, string Name);

/// <summary>
/// One of the user's signups in the active event, flattened across the
/// signup, its shift and the shift's rota.
/// </summary>
/// <param name="Id">Signup row id.</param>
/// <param name="ShiftId">The shift signed up for.</param>
/// <param name="SignupBlockId">
/// Set when the signup was created as part of a multi-day block; every row of
/// the block shares the value. <c>null</c> for a single-day signup.
/// </param>
/// <param name="Status">Signup status.</param>
/// <param name="RotaId">Owning rota id.</param>
/// <param name="RotaName">Owning rota display name; empty when the rota row is missing.</param>
/// <param name="RotaDescription">Rota description, if any.</param>
/// <param name="RotaPracticalInfo">Rota "where to show up / what to bring" text, if any.</param>
/// <param name="TeamId">Department team owning the rota.</param>
/// <param name="EventSettingsId">Event the rota belongs to.</param>
/// <param name="Date">Calendar date of the shift, resolved against the event's gate-opening date.</param>
/// <param name="Period">Build / Event / Strike, resolved against the event's dates.</param>
/// <param name="IsAllDay">Whether the shift is an all-day shift.</param>
/// <param name="WindowStart">Local start time — the all-day window start for an all-day shift.</param>
/// <param name="WindowEnd">Local end time — the all-day window end for an all-day shift.</param>
/// <param name="DurationHours">Shift length in hours.</param>
/// <param name="ShiftDescription">Per-shift duties text, if any.</param>
/// <param name="AbsoluteStart">Absolute start instant, resolved in the event's time zone.</param>
/// <param name="AbsoluteEnd">Absolute end instant, resolved in the event's time zone.</param>
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
    /// <summary>
    /// True while the signup is still a commitment —
    /// <see cref="SignupStatus.Pending"/> or <see cref="SignupStatus.Confirmed"/>.
    /// </summary>
    public bool IsActive => Status is SignupStatus.Pending or SignupStatus.Confirmed;
}
