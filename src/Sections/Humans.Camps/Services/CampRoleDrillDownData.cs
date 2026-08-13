namespace Humans.Camps.Services;

/// <summary>
/// Service-layer DTO returned by <see cref="Humans.Camps.Contracts.ICampRoleService.BuildDrillDownAsync"/>.
/// One row per camp-season for the resolved year, with assignees (display name + Google email)
/// for the requested role definition.
/// </summary>
internal sealed record CampRoleDrillDownData(
    Humans.Camps.Contracts.CampRoleDefinitionInfo Definition,
    int Year,
    string? GroupEmail,
    IReadOnlyList<CampRoleDrillDownCampRow> Rows);

internal sealed record CampRoleDrillDownCampRow(
    Guid CampId,
    string CampName,
    string CampSlug,
    Guid CampSeasonId,
    IReadOnlyList<CampRoleDrillDownAssignee> Assignees);

internal sealed record CampRoleDrillDownAssignee(
    Guid UserId,
    string? GoogleEmail,
    NodaTime.Instant AssignedAt);
