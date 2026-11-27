namespace Humans.Camps.Services;

internal sealed record CampRoleDrillDownData(
    CampRoleDefinitionInfo Definition,
    CampEventRegistration Event,
    string? GroupEmail,
    IReadOnlyList<CampRoleDrillDownCampRow> Rows);

internal sealed record CampRoleDrillDownCampRow(
    string CampName,
    string CampSlug,
    IReadOnlyList<CampRoleDrillDownAssignee> Assignees);

internal sealed record CampRoleDrillDownAssignee(
    Guid UserId,
    NodaTime.Instant AssignedAt);
