using Humans.Shifts.Contracts;
using NodaTime;

namespace Humans.Shifts.Services.Dtos;

/// <summary>
/// One rota on the department admin page: its header fields plus every shift with
/// its signups (all statuses). Returned by <see cref="IShiftManagement.GetRotasByDepartmentAsync"/>.
/// </summary>
internal sealed record ShiftAdminRota(RotaInfo Rota, IReadOnlyList<ShiftAdminShift> Shifts);

/// <summary>A shift on the admin page with its signups (all statuses).</summary>
internal sealed record ShiftAdminShift(ShiftInfo Shift, IReadOnlyList<ShiftAdminSignup> Signups)
{
    /// <summary>Confirmed signups on this shift.</summary>
    public int ConfirmedCount => Signups.Count(s => s.Status == SignupStatus.Confirmed);
}

/// <summary>A signup row as the admin page acts on it.</summary>
internal sealed record ShiftAdminSignup(
    Guid Id,
    Guid UserId,
    Guid? SignupBlockId,
    SignupStatus Status,
    Instant CreatedAt);

/// <summary>
/// A shift with its rota's header fields: the team-ownership and volunteer-search
/// read. Needs no event calendar, so it answers for every existing shift.
/// </summary>
internal sealed record ShiftWithRota(ShiftInfo Shift, RotaInfo Rota);
