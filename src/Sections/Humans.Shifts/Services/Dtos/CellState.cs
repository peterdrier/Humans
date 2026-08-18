namespace Humans.Shifts.Services.Dtos;

internal enum CellKind { Empty, Arrival, Worked }

internal sealed record CellState(CellKind Kind, Guid? TeamId = null, string? TeamColorHex = null)
{
    internal static CellState Empty { get; } = new(CellKind.Empty);
    internal static CellState Arrival { get; } = new(CellKind.Arrival);
    internal static CellState Worked(Guid teamId, string colorHex) => new(CellKind.Worked, teamId, colorHex);
}
