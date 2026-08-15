using Humans.Shifts.Domain;
using Humans.Shifts.Services.Dtos;

namespace Humans.Shifts.Services.Dtos;

/// <summary>
/// Cached per-rota projection: the rota row, its shifts, its tags, and every
/// signup on those shifts. Bundles raw EF rows only — no computed fields,
/// aggregates, or absolute-time resolution. Consumers compute what they need
/// from the raw rows.
/// </summary>
/// <remarks>
/// Returned by <see cref="Humans.Shifts.Services.IShiftRowView.GetRotaAsync"/> /
/// <see cref="Humans.Shifts.Services.IShiftRowView.GetRotasAsync"/>. Missing rotas yield an
/// empty view with <c>Rota = null</c> — never <c>null</c>, never an exception.
/// Issue #720.
/// </remarks>
internal sealed record ShiftRotaView(
    Guid RotaId,
    Rota? Rota,
    IReadOnlyList<Shift> Shifts,
    IReadOnlyList<ShiftTag> Tags,
    IReadOnlyList<ShiftSignup> Signups)
{
    /// <summary>
    /// Empty view returned for unknown rota ids.
    /// </summary>
    internal static ShiftRotaView Empty(Guid rotaId) => new(
        rotaId,
        Rota: null,
        Shifts: [],
        Tags: [],
        Signups: []);
}
