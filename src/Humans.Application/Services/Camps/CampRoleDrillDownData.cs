namespace Humans.Application.Services.Camps;

/// <summary>
/// Service-layer DTO returned by <see cref="Humans.Application.Interfaces.Camps.ICampRoleService.BuildDrillDownAsync"/>.
/// One row per camp-season for the resolved year, with assignees (display name + Google email)
/// for the requested role definition.
/// </summary>
public sealed record CampRoleDrillDownData(
    Humans.Application.Interfaces.Camps.CampRoleDefinitionInfo Definition,
    int Year,
    string? GroupEmail,
    IReadOnlyList<CampRoleDrillDownCampRow> Rows);

public sealed record CampRoleDrillDownCampRow(
    Guid CampId,
    string CampName,
    string CampSlug,
    Guid CampSeasonId,
    IReadOnlyList<CampRoleDrillDownAssignee> Assignees);

public sealed record CampRoleDrillDownAssignee(
    Guid UserId,
    string? GoogleEmail,
    NodaTime.Instant AssignedAt);
