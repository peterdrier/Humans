using Humans.Shifts.Domain;

namespace Humans.Shifts.Services.Dtos;

/// <summary>
/// Raw rows only: no computed fields or absolute times. A missing rota yields an empty view with a null
/// <c>Rota</c>, never null.
/// </summary>
internal sealed record ShiftRotaView(
    Guid RotaId,
    Rota? Rota,
    IReadOnlyList<Shift> Shifts,
    IReadOnlyList<ShiftTag> Tags,
    IReadOnlyList<ShiftSignup> Signups)
{
    internal static ShiftRotaView Empty(Guid rotaId) => new(
        rotaId,
        Rota: null,
        Shifts: [],
        Tags: [],
        Signups: []);
}
