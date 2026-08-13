namespace Humans.Shifts.Services.Dtos;

internal sealed record HumanRow(
    Guid UserId,
    string PlayaName,
    IReadOnlyList<CellState> Cells);
