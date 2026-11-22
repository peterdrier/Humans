namespace Humans.Shifts.Services.Dtos;

internal sealed record DepartmentGroup(
    Guid TeamId,
    string TeamName,
    string TeamColorHex,
    IReadOnlyList<HumanRow> Humans)
{
    public double TotalHours { get; init; }
}
