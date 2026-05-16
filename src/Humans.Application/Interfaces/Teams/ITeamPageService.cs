using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using NodaTime;

namespace Humans.Application.Interfaces.Teams;

public record TeamPageCustomPicture(Guid ProfileId, long UpdatedAtTicks);

public record TeamPageMemberSummary(
    Guid UserId,
    string DisplayName,
    string? Email,
    string? ProfilePictureUrl,
    TeamMemberRole Role,
    Instant? JoinedAt,
    TeamPageCustomPicture? CustomPicture);

public record TeamPageResourceSummary(
    string Name,
    string Url,
    GoogleResourceType ResourceType);

public record TeamPageShiftsSummary(
    int TotalSlots,
    int ConfirmedCount,
    int PendingCount,
    int UniqueVolunteerCount,
    bool CanManageShifts,
    int IncludesSubTeamCount = 0);

public record TeamPageTeamLink(Guid Id, string Name, string Slug);

public record TeamPageTeamSummary(
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

public record TeamPageDetailResult(
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
    TeamPageShiftsSummary? ShiftsSummary);

public interface ITeamPageService : IApplicationService
{
    Task<TeamPageDetailResult?> GetTeamPageDetailAsync(
        string slug,
        Guid? userId,
        bool canManageShiftsByRole,
        CancellationToken cancellationToken = default);
}
