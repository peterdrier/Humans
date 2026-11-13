using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
using Humans.Settings.Contracts;
namespace Humans.Shifts.Services.Dtos;

/// <summary>
/// No computed fields or absolute times. Empty, never null, for an unknown user or no active event.
/// All signups belong to the active event, so the one <see cref="Calendar"/> covers them.
/// </summary>
internal sealed record ShiftUserView(
    Guid UserId,
    VolunteerEventProfile? Profile,
    GeneralAvailability? Availability,
    VolunteerBuildStatus? BuildStatus,
    IReadOnlyList<VolunteerTagPreference> TagPreferences,
    IReadOnlyList<ShiftUserSignup> Signups,
    BurnSettingsInfo? Calendar = null)
{
    internal ShiftUserSummary ToSummary() => new(
        UserId,
        [.. TagPreferences.Select(p =>
            new ShiftTagPreferenceInfo(p.ShiftTagId, p.ShiftTag?.Name ?? string.Empty))],
        Calendar is null
            ? []
            : [.. Signups.Select(s => ShiftUserSummaryProjection.ToSummary(s, Calendar))]);
}

/// <summary>One of a user's signups with its shift and rota, as the cached rows hold them.</summary>
internal sealed record ShiftUserSignup(ShiftAdminSignup Signup, ShiftInfo Shift, RotaInfo Rota)
{
    public Guid Id => Signup.Id;
    public Guid ShiftId => Shift.Id;
    public SignupStatus Status => Signup.Status;
    public Guid? SignupBlockId => Signup.SignupBlockId;

    /// <summary>Needs <c>Shift.Rota</c> loaded.</summary>
    internal static ShiftUserSignup From(ShiftSignup signup) => new(
        new ShiftAdminSignup(signup.Id, signup.UserId, signup.SignupBlockId, signup.Status, signup.CreatedAt),
        ShiftBrowseReader.ToShiftInfo(signup.Shift),
        ShiftBrowseReader.ToRotaInfo(signup.Shift.Rota));
}
