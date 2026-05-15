using Humans.Domain.Entities;

namespace Humans.Application.DTOs.Shifts;

/// <summary>
/// Cached per-rota projection: the rota row, its shifts, its tags, and every
/// signup on those shifts. Bundles raw EF rows only — no computed fields,
/// aggregates, or absolute-time resolution. Consumers compute what they need
/// from the raw rows.
/// </summary>
/// <remarks>
/// Returned by <see cref="Interfaces.Shifts.IShiftView.GetRota"/> /
/// <see cref="Interfaces.Shifts.IShiftView.GetRotas"/>. Missing rotas yield an
/// empty view with <c>Rota = null</c> — never <c>null</c>, never an exception.
/// Issue #720.
/// </remarks>
public sealed record ShiftRotaView(
    Guid RotaId,
    Rota? Rota,
    IReadOnlyList<Shift> Shifts,
    IReadOnlyList<ShiftTag> Tags,
    IReadOnlyList<ShiftSignup> Signups)
{
    /// <summary>
    /// Empty view returned for unknown rota ids.
    /// </summary>
    public static ShiftRotaView Empty(Guid rotaId) => new(
        rotaId,
        Rota: null,
        Shifts: [],
        Tags: [],
        Signups: []);
}
