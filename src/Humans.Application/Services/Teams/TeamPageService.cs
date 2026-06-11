using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Teams;

public sealed class TeamPageService(
    ITeamService teamService,
    ITeamResourceService teamResourceService,
    IShiftManagementService shiftManagementService,
    IUserServiceRead userService) : ITeamPageService
{
    public async Task<TeamPageDetailResult?> GetTeamPageDetailAsync(
        string slug,
        Guid? userId,
        bool canManageShiftsByRole,
        CancellationToken cancellationToken = default)
    {
        var detail = await teamService.GetTeamDetailAsync(slug, userId, cancellationToken);
        if (detail is null)
        {
            return null;
        }

        var visibleMembers = detail.IsAuthenticated
            ? detail.Members
            : detail.Team.ShowCoordinatorsOnPublicPage
                ? detail.Members.Where(m => m.Role == TeamMemberRole.Coordinator).ToList()
                : [];

        var members = visibleMembers
            .Select(member => new TeamPageMemberSummary(
                member.UserId,
                member.DisplayName,
                detail.IsAuthenticated ? member.Email : null,
                member.ProfilePictureUrl,
                member.Role,
                detail.IsAuthenticated ? member.JoinedAt : null))
            .ToList();

        var pageContentUpdatedBy = detail.Team.PageContentUpdatedByUserId.HasValue
            ? await userService.GetUserInfoAsync(detail.Team.PageContentUpdatedByUserId.Value, cancellationToken)
            : null;

        var resources = detail.IsAuthenticated
            ? (await teamResourceService.GetTeamResourcesAsync(detail.Team.Id, cancellationToken))
                .Select(resource => new TeamPageResourceSummary(
                    resource.Name,
                    resource.Url ?? string.Empty,
                    resource.ResourceType))
                .ToList()
            : [];

        var shiftsSummary = await GetShiftsSummaryAsync(
            detail.Team,
            detail.ChildTeams,
            userId,
            detail.IsAuthenticated,
            canManageShiftsByRole);

        return new TeamPageDetailResult(
            detail.Team,
            members,
            detail.ChildTeams,
            detail.RoleDefinitions,
            resources,
            detail.IsAuthenticated,
            detail.IsCurrentUserMember,
            detail.IsCurrentUserCoordinator,
            detail.CanCurrentUserJoin,
            detail.CanCurrentUserLeave,
            detail.CanCurrentUserManage,
            detail.CanCurrentUserEditTeam,
            detail.CurrentUserPendingRequestId,
            detail.PendingRequestCount,
            pageContentUpdatedBy?.BurnerName,
            shiftsSummary);
    }

    private async Task<TeamPageShiftsSummary?> GetShiftsSummaryAsync(
        TeamPageTeamSummary team,
        IReadOnlyList<TeamPageTeamLink> childTeams,
        Guid? userId,
        bool isAuthenticated,
        bool canManageShiftsByRole)
    {
        if (!isAuthenticated ||
            !userId.HasValue ||
            team.SystemTeamType != SystemTeamType.None)
        {
            return null;
        }

        var canManageShifts = canManageShiftsByRole ||
            await shiftManagementService.IsDeptCoordinatorAsync(userId.Value, team.Id);

        var activeEvent = await shiftManagementService.GetActiveAsync();
        if (activeEvent is null)
        {
            return new TeamPageShiftsSummary(0, 0, 0, 0, canManageShifts);
        }

        var activeChildTeamIds = childTeams.Select(c => c.Id).ToList();
        var teamIds = new List<Guid>(activeChildTeamIds.Count + 1) { team.Id };
        teamIds.AddRange(activeChildTeamIds);

        var summaryData = await shiftManagementService.GetShiftsSummaryAsync(activeEvent.Id, teamIds);
        if (summaryData is null)
        {
            return new TeamPageShiftsSummary(0, 0, 0, 0, canManageShifts);
        }

        var childTeamCountWithShifts = activeChildTeamIds
            .Count(summaryData.TeamIdsWithShifts.Contains);

        return new TeamPageShiftsSummary(
            summaryData.TotalSlots,
            summaryData.ConfirmedCount,
            summaryData.PendingCount,
            summaryData.UniqueVolunteerCount,
            canManageShifts,
            childTeamCountWithShifts);
    }
}
