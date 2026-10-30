namespace Humans.Camps.Services;

/// <summary>Assignees join by <c>CampRoleDefinitionId</c>, never by role name.</summary>
internal sealed record CampComplianceMatrixData(
    CampEventRegistration Event,
    IReadOnlyList<CampRoleDefinitionInfo> Roles,
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
