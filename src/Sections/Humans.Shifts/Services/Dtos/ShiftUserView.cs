using Humans.Shifts.Domain;
using Humans.Shifts.Contracts;
using Humans.Settings.Contracts;
namespace Humans.Shifts.Services.Dtos;

/// <summary>
/// Raw rows only: no computed fields or absolute times. Empty, never null, for an unknown user or no active event.
/// All signups belong to the active event, so the one <see cref="Calendar"/> covers them.
/// </summary>
internal sealed record ShiftUserView(
    Guid UserId,
    VolunteerEventProfile? Profile,
    GeneralAvailability? Availability,
    VolunteerBuildStatus? BuildStatus,
    IReadOnlyList<VolunteerTagPreference> TagPreferences,
    IReadOnlyList<ShiftSignup> Signups,
    EventSettingsInfo? Calendar = null)
{
    /// <summary>Pending or Confirmed; the other statuses are no longer commitments.</summary>
    public bool HasShift => Signups.Any(s =>
        s.Status is SignupStatus.Pending or SignupStatus.Confirmed);

    public int ActiveSignupCount => Signups.Count(s =>
        s.Status is SignupStatus.Pending or SignupStatus.Confirmed);

    /// <summary>
    /// Active signups only. With no <see cref="Calendar"/> (no active event, or none in Settings yet) nothing can be
    /// classified.
    /// </summary>
    public bool HasShiftInPeriod(ShiftPeriod period) => Calendar is not null && Signups.Any(s =>
        s.Status is SignupStatus.Pending or SignupStatus.Confirmed
        && s.Shift.GetShiftPeriod(Calendar) == period);

    internal static ShiftUserView Empty(Guid userId) => new(
        userId,
        Profile: null,
        Availability: null,
        BuildStatus: null,
        TagPreferences: [],
        Signups: []);
}
