using Humans.GoogleIntegration.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Domain.Enums;
using Humans.Users.Contracts;

namespace Humans.Teams.Services;

internal sealed class TeamPageService(
    ITeamManagementService teamService,
    ITeamResourceService teamResourceService,
    IShiftManagementServiceRead shiftManagementService,
    IBurnSettingsService burnSettings,
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

        var (subteamLeads, subteamMembers) = detail.IsAuthenticated && detail.ChildTeams.Count > 0
            ? await BuildSubteamRollupAsync(detail.ChildTeams, members)
            : ([], []);

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
            shiftsSummary,
            subteamLeads,
            subteamMembers);
    }

    /// <summary>
    /// Department page's subteam-member rollup: every child-team coordinator
    /// (as a "lead"), plus every other child-team member not already a direct
    /// department member (deduplicated across multiple child teams).
    /// </summary>
    private async Task<(IReadOnlyList<TeamPageChildTeamMemberSummary> Leads, IReadOnlyList<TeamPageChildTeamMemberSummary> Members)>
        BuildSubteamRollupAsync(
            IReadOnlyList<TeamPageTeamLink> childTeams,
            IReadOnlyList<TeamPageMemberSummary> directMembers)
    {
        var directMemberUserIds = new HashSet<Guid>(directMembers.Select(m => m.UserId));
        var addedUserIds = new HashSet<Guid>();

        var childTeamIds = childTeams.Select(c => c.Id).ToList();
        var managementRolesByTeam = await teamService.GetManagementRoleNamesByTeamIdsAsync(childTeamIds);
        var teamsById = await teamService.GetTeamsAsync();

        var childMembersByTeam = childTeamIds
            .Where(teamsById.ContainsKey)
            .ToDictionary(id => id, id => teamsById[id].Members);

        var leads = new List<TeamPageChildTeamMemberSummary>();
        var rollupMembers = new List<TeamPageChildTeamMemberSummary>();

        foreach (var child in childTeams)
        {
            if (!childMembersByTeam.TryGetValue(child.Id, out var childMembers))
                continue;
            var managementRoleName = managementRolesByTeam.GetValueOrDefault(child.Id);

            foreach (var cm in childMembers)
            {
                var isCoordinator = cm.Role == TeamMemberRole.Coordinator;

                if (isCoordinator)
                    leads.Add(new TeamPageChildTeamMemberSummary(cm.UserId, child.Name, child.Slug, true, managementRoleName));

                if (directMemberUserIds.Contains(cm.UserId) || !addedUserIds.Add(cm.UserId))
                    continue;

                rollupMembers.Add(new TeamPageChildTeamMemberSummary(
                    cm.UserId, child.Name, child.Slug, isCoordinator, isCoordinator ? managementRoleName : null));
            }
        }

        return (leads, rollupMembers);
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

        var activeEvent = await burnSettings.GetActiveAsync();
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
