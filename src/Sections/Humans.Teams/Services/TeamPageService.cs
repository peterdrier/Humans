using Humans.Auth.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Base.Enums;
using Humans.Users.Contracts;

namespace Humans.Teams.Services;

internal sealed class TeamPageService(
    ITeamManagementService teamService,
    ITeamResourceService teamResourceService,
    IShiftManagementService shiftManagementService,
    ISettingsService settingsService,
    IUserService userService,
    IRoleAssignmentService roleAssignmentService) : ITeamPageService
{
    public async Task<TeamPageDetailResult?> GetTeamPageDetailAsync(
        string slug,
        Guid? userId,
        bool canManageShiftsByRole,
        CancellationToken cancellationToken = default)
    {
        var detail = await GetTeamDetailAsync(slug, userId, cancellationToken);
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
                    resource.ResourceType,
                    resource.ProvisionedAt))
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

    public async Task<IReadOnlyList<MyTeamMembershipSummary>> GetMyTeamMembershipsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var teamsById = await teamService.GetTeamsAsync(cancellationToken);
        var teamIds = (await teamService.GetUserTeamMembershipsAsync(userId, cancellationToken))
            .Select(membership => membership.TeamId)
            .ToList();
        if (teamIds.Count == 0) return [];

        var isBoardMember = await roleAssignmentService.IsUserBoardMemberAsync(userId, cancellationToken);
        var canSeeHiddenTeams = isBoardMember
            || await roleAssignmentService.IsUserAdminAsync(userId, cancellationToken)
            || await roleAssignmentService.IsUserTeamsAdminAsync(userId, cancellationToken);

        var result = new List<MyTeamMembershipSummary>(teamIds.Count);
        foreach (var teamId in teamIds)
        {
            if (!teamsById.TryGetValue(teamId, out var team) || (team.IsHidden && !canSeeHiddenTeams))
                continue;

            var membership = team.Members.FirstOrDefault(m => m.UserId == userId);
            if (membership is null)
                continue;

            // Inherited coordinator: a user can manage a team if they're a
            // direct Coordinator, a board member, or coordinate any ancestor
            // team (matches CanUserApproveRequestsForTeamAsync's recursive
            // parent walk via IsUserCoordinatorOfActiveTeam).
            var canManage =
                (isBoardMember || TeamCoordinatorAccess.IsCoordinatorOfActiveTeam(teamsById, team.Id, userId))
                && !team.IsSystemTeam;

            var pendingCount = 0;
            if (canManage)
            {
                pendingCount += team.PendingRequestCount;
                if (team.ChildTeamIds is not null)
                {
                    foreach (var childId in team.ChildTeamIds)
                    {
                        if (!teamsById.TryGetValue(childId, out var child) || !child.IsActive
                            || (child.IsHidden && !canSeeHiddenTeams))
                            continue;
                        pendingCount += child.PendingRequestCount;
                    }
                }
            }

            var displayName = team.ParentTeamId.HasValue
                && teamsById.TryGetValue(team.ParentTeamId.Value, out var parent)
                && (!parent.IsHidden || canSeeHiddenTeams)
                ? $"{parent.Name} - {team.Name}"
                : team.Name;

            result.Add(new MyTeamMembershipSummary(
                team.Id,
                displayName,
                team.Slug,
                team.IsSystemTeam,
                membership.Role,
                membership.JoinedAt,
                CanLeave: !team.IsSystemTeam,
                PendingRequestCount: pendingCount));
        }

        return result;
    }

    internal async Task<TeamDetailResult?> GetTeamDetailAsync(
        string slug,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        // TeamInfo is the canonical cached read shape; pending requests stay live reads.
        var teamsById = await teamService.GetTeamsAsync(cancellationToken);
        var team = TeamSlugResolver.Find(teamsById, slug);
        if (team is null)
            return null;

        TeamInfo? parent = team.ParentTeamId.HasValue
            ? teamsById.GetValueOrDefault(team.ParentTeamId.Value)
            : null;

        if (!userId.HasValue)
        {
            var isAnonymouslyVisible = !team.IsHidden && !team.IsSystemTeam
                && ((team.ParentTeamId is null && team.IsPublicPage)
                    || (team.ParentTeamId is not null && team.IsPromotedToDirectory));
            if (!isAnonymouslyVisible)
                return null;

            var coordinators = team.Members
                .Where(m => m.Role == TeamMemberRole.Coordinator)
                .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(MapTeamDetailMemberSummary)
                .ToList();

            return new TeamDetailResult(
                Team: MapTeamSummary(team, parent?.IsHidden == true ? null : parent),
                Members: coordinators,
                ChildTeams: EnumerateChildren(team, teamsById)
                    .Where(c => c.IsActive && c.IsPublicPage && !c.IsHidden)
                    .OrderBy(c => c.Name, StringComparer.Ordinal)
                    .Select(MapTeamLink)
                    .ToList(),
                RoleDefinitions: [],
                IsAuthenticated: false,
                IsCurrentUserMember: false,
                IsCurrentUserCoordinator: false,
                CanCurrentUserJoin: false,
                CanCurrentUserLeave: false,
                CanCurrentUserManage: false,
                CanCurrentUserEditTeam: false,
                CurrentUserPendingRequestId: null,
                PendingRequestCount: 0);
        }

        var currentUserId = userId.Value;
        var isCurrentUserMember = team.Members.Any(m => m.UserId == currentUserId);
        var isCurrentUserCoordinator = TeamCoordinatorAccess.IsCoordinatorOfActiveTeam(teamsById, team.Id, currentUserId);

        var isBoardMember = await roleAssignmentService.IsUserBoardMemberAsync(currentUserId, cancellationToken);
        var isAdmin = await roleAssignmentService.IsUserAdminAsync(currentUserId, cancellationToken);
        var isTeamsAdmin = await roleAssignmentService.IsUserTeamsAdminAsync(currentUserId, cancellationToken);
        var canSeeHiddenTeams = isBoardMember || isAdmin || isTeamsAdmin;
        var canManage = isCurrentUserCoordinator || canSeeHiddenTeams;

        if (team.IsHidden && !canSeeHiddenTeams)
            return null;

        var pendingRequest = await teamService.GetUserPendingRequestAsync(team.Id, currentUserId, cancellationToken);
        var pendingRequestCount = canManage
            ? (await teamService.GetPendingRequestsForTeamAsync(team.Id, cancellationToken)).Count
            : 0;

        return new TeamDetailResult(
            Team: MapTeamSummary(team, parent?.IsHidden == true && !canSeeHiddenTeams ? null : parent),
            Members: team.Members
                .OrderBy(m => m.Role)
                .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(MapTeamDetailMemberSummary)
                .ToList(),
            ChildTeams: EnumerateChildren(team, teamsById)
                .Where(c => c.IsActive && (!c.IsHidden || canSeeHiddenTeams))
                .OrderBy(c => c.Name, StringComparer.Ordinal)
                .Select(MapTeamLink)
                .ToList(),
            RoleDefinitions: team.RoleDefinitions ?? [],
            IsAuthenticated: true,
            IsCurrentUserMember: isCurrentUserMember,
            IsCurrentUserCoordinator: isCurrentUserCoordinator,
            CanCurrentUserJoin: team.IsActive && !isCurrentUserMember && !team.IsSystemTeam && pendingRequest is null,
            CanCurrentUserLeave: isCurrentUserMember && !team.IsSystemTeam,
            CanCurrentUserManage: canManage,
            CanCurrentUserEditTeam: isBoardMember || isAdmin || isTeamsAdmin,
            CurrentUserPendingRequestId: pendingRequest?.Id,
            PendingRequestCount: pendingRequestCount);
    }

    private static IEnumerable<TeamInfo> EnumerateChildren(
        TeamInfo team,
        IReadOnlyDictionary<Guid, TeamInfo> teamsById)
    {
        if (team.ChildTeamIds is null || team.ChildTeamIds.Count == 0)
            yield break;
        foreach (var childId in team.ChildTeamIds)
        {
            if (teamsById.TryGetValue(childId, out var child))
                yield return child;
        }
    }

    private static TeamPageTeamSummary MapTeamSummary(TeamInfo team, TeamInfo? parent) =>
        TeamPageSummaryMapper.Map(
            id: team.Id,
            name: team.Name,
            parentName: parent?.Name,
            description: team.Description,
            slug: team.Slug,
            isActive: team.IsActive,
            requiresApproval: team.RequiresApproval,
            isSystemTeam: team.IsSystemTeam,
            systemTeamType: team.SystemTeamType,
            createdAt: team.CreatedAt,
            isPublicPage: team.IsPublicPage,
            showCoordinatorsOnPublicPage: team.ShowCoordinatorsOnPublicPage,
            pageContent: team.PageContent,
            callsToAction: team.CallsToAction is null ? [] : [.. team.CallsToAction],
            pageContentUpdatedAt: team.PageContentUpdatedAt,
            pageContentUpdatedByUserId: team.PageContentUpdatedByUserId,
            parentLink: parent is null ? null : new TeamPageTeamLink(parent.Id, parent.Name, parent.Slug));

    private static TeamPageTeamLink MapTeamLink(TeamInfo team) =>
        new(team.Id, team.Name, team.Slug);

    private static TeamDetailMemberSummary MapTeamDetailMemberSummary(TeamMemberInfo member) =>
        new(
            UserId: member.UserId,
            DisplayName: member.DisplayName,
            Email: member.Email,
            ProfilePictureUrl: member.ProfilePictureUrl,
            Role: member.Role,
            JoinedAt: member.JoinedAt);

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

        var activeEvent = await settingsService.GetActiveEventSettingsAsync();
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
