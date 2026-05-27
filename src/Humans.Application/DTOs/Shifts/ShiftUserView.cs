using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.DTOs.Shifts;

/// <summary>
/// Cached per-user projection of every Shifts-section row keyed off
/// <see cref="UserId"/>. Bundles raw EF rows only — no computed fields,
/// aggregates, or absolute-time resolution. Consumers compute what they need
/// from the raw rows.
/// </summary>
/// <remarks>
/// Returned by <see cref="Interfaces.Shifts.IShiftView.GetUser"/> /
/// <see cref="Interfaces.Shifts.IShiftView.GetUsers"/>. Missing users (or
/// "no active event") yield an empty view — never <c>null</c>, never an
/// exception. Issue #720.
/// </remarks>
public sealed record ShiftUserView(
    Guid UserId,
    VolunteerEventProfile? Profile,
    GeneralAvailability? Availability,
    VolunteerBuildStatus? BuildStatus,
    IReadOnlyList<VolunteerTagPreference> TagPreferences,
    IReadOnlyList<ShiftSignup> Signups)
{
    /// <summary>
    /// True when the user has at least one signup in an active state
    /// (<see cref="SignupStatus.Pending"/> or <see cref="SignupStatus.Confirmed"/>)
    /// in the active event. Refused / Bailed / Cancelled / NoShow signups
    /// don't count — they're no longer commitments. Mirrors the convention
    /// used by <c>ShiftRepository</c>, <c>ShiftManagementService</c>,
    /// and the agent snapshot.
    /// </summary>
    public bool HasShift => Signups.Any(s =>
        s.Status is SignupStatus.Pending or SignupStatus.Confirmed);

    /// <summary>
    /// Empty view returned for unknown ids / no active event.
    /// </summary>
    public static ShiftUserView Empty(Guid userId) => new(
        userId,
        Profile: null,
        Availability: null,
        BuildStatus: null,
        TagPreferences: [],
        Signups: []);
}
