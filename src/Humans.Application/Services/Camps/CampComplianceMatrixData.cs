namespace Humans.Application.Services.Camps;

/// <summary>
/// Service-layer DTO returned by <see cref="Humans.Application.Interfaces.Camps.ICampRoleService.BuildComplianceMatrixAsync"/>.
/// One column per active role definition (canonical SortOrder ordering), one row per
/// Active/Full camp season in the year, with assignees joined by
/// <c>CampRoleDefinitionId</c> — never by role name.
/// </summary>
public sealed record CampComplianceMatrixData(
    int Year,
    IReadOnlyList<Humans.Application.Interfaces.Camps.CampRoleDefinitionInfo> Roles,
    IReadOnlyList<CampComplianceMatrixRow> Rows);

/// <summary>
/// One Active/Full camp season. <see cref="AssigneeUserIdsByRole"/> is parallel to
/// <see cref="CampComplianceMatrixData.Roles"/>.
/// </summary>
public sealed record CampComplianceMatrixRow(
    string CampName,
    string CampSlug,
    int JoinedMemberCount,
    int TargetMemberCount,
    IReadOnlyList<IReadOnlyList<Guid>> AssigneeUserIdsByRole);
