namespace Humans.Camps.Models;

internal sealed class CampComplianceViewModel
{
    public required int Year { get; init; }

    /// <summary>Role columns, in display order (one per active role definition).</summary>
    public required IReadOnlyList<CampComplianceRoleColumn> Roles { get; init; }

    /// <summary>One row per active barrio, sorted by name.</summary>
    public required IReadOnlyList<CampComplianceRow> Rows { get; init; }

    public int BarrioCount => Rows.Count;

    public int NonCompliantCount => Rows.Count(r => r.IsBelowMinimum);
}

internal sealed record CampComplianceRoleColumn(Guid DefinitionId, string Name, int MinimumRequired);

internal sealed record CampComplianceRow(
    string CampName,
    string CampSlug,
    int JoinedMemberCount,
    int TargetMemberCount,
    IReadOnlyList<CampComplianceRoleCell> Cells)
{
    /// <summary>True when any role is below its required minimum.</summary>
    public bool IsBelowMinimum => Cells.Any(c => c.IsBelowMinimum);
}

internal sealed record CampComplianceRoleCell(
    IReadOnlyList<Guid> AssigneeUserIds,
    int MinimumRequired)
{
    /// <summary>How many more holders this role needs to meet its minimum (0 when met or not required).</summary>
    public int Shortfall => Math.Max(0, MinimumRequired - AssigneeUserIds.Count);

    public bool IsBelowMinimum => Shortfall > 0;
}
