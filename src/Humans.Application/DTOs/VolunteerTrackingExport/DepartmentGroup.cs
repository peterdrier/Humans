namespace Humans.Application.DTOs.VolunteerTrackingExport;

public sealed record DepartmentGroup(
    Guid TeamId,
    string TeamName,
    string TeamColorHex,
    IReadOnlyList<HumanRow> Humans);
