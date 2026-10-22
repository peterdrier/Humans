using Humans.Shifts.Contracts;
using NodaTime;

namespace Humans.Shifts.Services.Dtos;

/// <summary>Every shift with its signups, all statuses.</summary>
internal sealed record ShiftAdminRota(RotaInfo Rota, IReadOnlyList<ShiftAdminShift> Shifts);

internal sealed record ShiftAdminShift(ShiftInfo Shift, IReadOnlyList<ShiftAdminSignup> Signups)
{
    public int ConfirmedCount => Signups.Count(s => s.Status == SignupStatus.Confirmed);
}

internal sealed record ShiftAdminSignup(
    Guid Id,
    Guid UserId,
    Guid? SignupBlockId,
    SignupStatus Status,
    Instant CreatedAt);

/// <summary>Needs no event calendar, so it answers for every existing shift.</summary>
internal sealed record ShiftWithRota(ShiftInfo Shift, RotaInfo Rota);
