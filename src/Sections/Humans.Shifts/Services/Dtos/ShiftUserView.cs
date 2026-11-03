using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
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
    BurnSettingsInfo? Calendar = null);
