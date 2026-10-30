namespace Humans.Camps.Services;

internal sealed record CampRoleDrillDownData(
    CampRoleDefinitionInfo Definition,
    CampEventRegistration Event,
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
