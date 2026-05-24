namespace Humans.Application.DTOs.VolunteerTrackingExport;

public sealed record HumanRow(
    Guid UserId,
    string PlayaName,
    IReadOnlyList<CellState> Cells);
