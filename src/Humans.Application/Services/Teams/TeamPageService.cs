using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Teams;

public sealed class TeamPageService : ITeamPageService
{
    private readonly ITeamService _teamService;
    private readonly ITeamResourceService _teamResourceService;
    private readonly IShiftManagementService _shiftManagementService;
    private readonly IUserService _userService;

    public TeamPageService(
        ITeamService teamService,
        ITeamResourceService teamResourceService,
        IShiftManagementService shiftManagementService,
        IUserService userService)
    {
        _teamService = teamService;
        _teamResourceService = teamResourceService;
        _shiftManagementService = shiftManagementService;
        _userService = userService;
    }

    public async Task<TeamPageDetailResult?> GetTeamPageDetailAsync(
        string slug,
        Guid? userId,
        bool canManageShiftsByRole,
        CancellationToken cancellationToken = default)
    {
        var detail = await _teamService.GetTeamDetailAsync(slug, userId, cancellationToken);
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
            ? (await _teamResourceService.GetTeamResourcesAsync(detail.Team.Id, cancellationToken))
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

        var user = await _userService.GetByIdAsync(userId.Value, cancellationToken);
        return user?.DisplayName;
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
            await _shiftManagementService.IsDeptCoordinatorAsync(userId.Value, team.Id);

        var activeEvent = await _shiftManagementService.GetActiveAsync();
        if (activeEvent is null)
        {
            return new TeamPageShiftsSummary(0, 0, 0, 0, canManageShifts);
        }

        var activeChildTeamIds = childTeams.Select(c => c.Id).ToList();

        if (activeChildTeamIds.Count > 0)
        {
            var allTeamIds = new List<Guid>(activeChildTeamIds.Count + 1) { team.Id };
            allTeamIds.AddRange(activeChildTeamIds);

            var aggregatedData = await _shiftManagementService.GetShiftsSummaryAsync(activeEvent.Id, allTeamIds);
            if (aggregatedData is null)
            {
                return new TeamPageShiftsSummary(0, 0, 0, 0, canManageShifts);
            }

            var childTeamIdsWithShifts = await _shiftManagementService.GetTeamIdsWithShiftsInEventAsync(
                activeEvent.Id, activeChildTeamIds, cancellationToken);

            return new TeamPageShiftsSummary(
                aggregatedData.TotalSlots,
                aggregatedData.ConfirmedCount,
                aggregatedData.PendingCount,
                aggregatedData.UniqueVolunteerCount,
                canManageShifts,
                childTeamIdsWithShifts.Count);
        }

        var summaryData = await _shiftManagementService.GetShiftsSummaryAsync(activeEvent.Id, [team.Id]);
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
