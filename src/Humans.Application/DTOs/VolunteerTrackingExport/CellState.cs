namespace Humans.Application.DTOs.VolunteerTrackingExport;

public enum CellKind { Empty, Arrival, Worked }

public sealed record CellState(CellKind Kind, Guid? TeamId = null, string? TeamColorHex = null)
{
    public static CellState Empty { get; } = new(CellKind.Empty);
    public static CellState Arrival { get; } = new(CellKind.Arrival);
    public static CellState Worked(Guid teamId, string colorHex) => new(CellKind.Worked, teamId, colorHex);
}
