namespace Humans.Camps.Services;

/// <summary>
/// Service-layer DTO returned by <see cref="Humans.Camps.Contracts.ICampRoleService.BuildComplianceMatrixAsync"/>.
/// One column per active role definition (canonical SortOrder ordering), one row per
/// Active/Full camp season in the year, with assignees joined by
/// <c>CampRoleDefinitionId</c> — never by role name.
/// </summary>
internal sealed record CampComplianceMatrixData(
    int Year,
    IReadOnlyList<Humans.Camps.Contracts.CampRoleDefinitionInfo> Roles,
    IReadOnlyList<CampComplianceMatrixRow> Rows);

/// <summary>
/// One Active/Full camp season. <see cref="AssigneeUserIdsByRole"/> is parallel to
/// <see cref="CampComplianceMatrixData.Roles"/>.
/// </summary>
internal sealed record CampComplianceMatrixRow(
    string CampName,
    string CampSlug,
    int JoinedMemberCount,
    int TargetMemberCount,
    IReadOnlyList<IReadOnlyList<Guid>> AssigneeUserIdsByRole);
