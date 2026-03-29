using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces;

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
    bool CanManageShifts);

public record TeamPageDetailResult(
    Team Team,
    IReadOnlyList<TeamPageMemberSummary> Members,
    IReadOnlyList<Team> ChildTeams,
    IReadOnlyList<TeamRoleDefinition> RoleDefinitions,
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

public interface ITeamPageService
{
    Task<TeamPageDetailResult?> GetTeamPageDetailAsync(
        string slug,
        Guid? userId,
        bool canManageShiftsByRole,
        CancellationToken cancellationToken = default);
}
