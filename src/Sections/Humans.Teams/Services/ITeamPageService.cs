using Humans.GoogleIntegration.Contracts;
using Humans.Base.Interfaces;
using Humans.Teams.Contracts;
using Humans.Base.Enums;
using NodaTime;

namespace Humans.Teams.Services;

internal sealed record TeamPageMemberSummary(
    Guid UserId,
    string DisplayName,
    string? Email,
    string? ProfilePictureUrl,
    TeamMemberRole Role,
    Instant? JoinedAt);

internal sealed record TeamPageResourceSummary(
    string Name,
    string Url,
    GoogleResourceType ResourceType);

internal sealed record TeamPageShiftsSummary(
    int TotalSlots,
    int ConfirmedCount,
    int PendingCount,
    int UniqueVolunteerCount,
    bool CanManageShifts,
    int IncludesSubTeamCount = 0);

internal sealed record TeamPageTeamLink(Guid Id, string Name, string Slug);

/// <summary>
/// One row of the department page's subteam-member rollup: a child-team member
/// (coordinator lead, or an ordinary member not already a direct department
/// member). <see cref="RoleTitle"/> is the child team's management role name,
/// populated only for coordinators.
/// </summary>
internal sealed record TeamPageChildTeamMemberSummary(
    Guid UserId,
    string ChildTeamName,
    string ChildTeamSlug,
    bool IsCoordinator,
    string? RoleTitle);

internal sealed record TeamPageTeamSummary(
    Guid Id,
    string Name,
    string DisplayName,
    string? Description,
    string Slug,
    bool IsActive,
    bool RequiresApproval,
    bool IsSystemTeam,
    SystemTeamType SystemTeamType,
    Instant CreatedAt,
    bool IsPublicPage,
    bool ShowCoordinatorsOnPublicPage,
    string? PageContent,
    List<CallToAction> CallsToAction,
    Instant? PageContentUpdatedAt,
    Guid? PageContentUpdatedByUserId,
    TeamPageTeamLink? ParentTeam);

internal sealed record TeamPageDetailResult(
    TeamPageTeamSummary Team,
    IReadOnlyList<TeamPageMemberSummary> Members,
    IReadOnlyList<TeamPageTeamLink> ChildTeams,
    IReadOnlyList<TeamRoleDefinitionSnapshot> RoleDefinitions,
    IReadOnlyList<TeamPageResourceSummary> Resources,
    bool IsAuthenticated,
    bool IsCurrentUserMember,
    bool IsCurrentUserCoordinator,
    bool CanCurrentUserJoin,
    bool CanCurrentUserLeave,
    bool CanCurrentUserManage,
    bool CanCurrentUserEditTeam,
    Guid? CurrentUserPendingRequestId,
    int PendingRequestCount,
    string? PageContentUpdatedByDisplayName,
    TeamPageShiftsSummary? ShiftsSummary,
    IReadOnlyList<TeamPageChildTeamMemberSummary> SubteamLeads,
    IReadOnlyList<TeamPageChildTeamMemberSummary> SubteamMembers);

internal interface ITeamPageService : IApplicationService
{
    Task<TeamPageDetailResult?> GetTeamPageDetailAsync(
        string slug,
        Guid? userId,
        bool canManageShiftsByRole,
        CancellationToken cancellationToken = default);
}
