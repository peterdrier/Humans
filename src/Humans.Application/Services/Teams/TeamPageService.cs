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
    IUserService userService) : ITeamPageService
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

        var pageContentUpdatedByDisplayName = await GetPageContentUpdatedByDisplayNameAsync(
            detail.Team.PageContentUpdatedByUserId,
            cancellationToken);

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
            canManageShiftsByRole,
            cancellationToken);

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
            pageContentUpdatedByDisplayName,
            shiftsSummary);
    }

    private async Task<string?> GetPageContentUpdatedByDisplayNameAsync(
        Guid? userId,
        CancellationToken cancellationToken)
    {
        if (!userId.HasValue)
        {
            return null;
        }

        var user = await userService.GetUserInfoAsync(userId.Value, cancellationToken);
        return user?.BurnerName;
    }

    private async Task<TeamPageShiftsSummary?> GetShiftsSummaryAsync(
        TeamPageTeamSummary team,
        IReadOnlyList<TeamPageTeamLink> childTeams,
        Guid? userId,
        bool isAuthenticated,
        bool canManageShiftsByRole,
        CancellationToken cancellationToken)
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

        if (activeChildTeamIds.Count > 0)
        {
            var allTeamIds = new List<Guid>(activeChildTeamIds.Count + 1) { team.Id };
            allTeamIds.AddRange(activeChildTeamIds);

            var aggregatedData = await shiftManagementService.GetShiftsSummaryAsync(activeEvent.Id, allTeamIds);
            if (aggregatedData is null)
            {
                return new TeamPageShiftsSummary(0, 0, 0, 0, canManageShifts);
            }

            var childTeamIdsWithShifts = await shiftManagementService.GetTeamIdsWithShiftsInEventAsync(
                activeEvent.Id, activeChildTeamIds, cancellationToken);

            return new TeamPageShiftsSummary(
                aggregatedData.TotalSlots,
                aggregatedData.ConfirmedCount,
                aggregatedData.PendingCount,
                aggregatedData.UniqueVolunteerCount,
                canManageShifts,
                childTeamIdsWithShifts.Count);
        }

        var summaryData = await shiftManagementService.GetShiftsSummaryAsync(activeEvent.Id, [team.Id]);
        if (summaryData is null)
        {
            return new TeamPageShiftsSummary(0, 0, 0, 0, canManageShifts);
        }

        return new TeamPageShiftsSummary(
            summaryData.TotalSlots,
            summaryData.ConfirmedCount,
            summaryData.PendingCount,
            summaryData.UniqueVolunteerCount,
            canManageShifts);
    }
}
