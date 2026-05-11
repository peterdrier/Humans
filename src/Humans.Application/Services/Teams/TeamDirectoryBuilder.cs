using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Teams;

public static class TeamDirectoryBuilder
{
    public static async Task<TeamDirectoryResult> BuildAsync(
        IReadOnlyDictionary<Guid, TeamInfo> teamsById,
        IRoleAssignmentService roleAssignmentService,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        if (!userId.HasValue)
        {
            var publicDepartments = teamsById.Values
                .Where(t => !t.IsSystemTeam && !t.IsHidden
                    && t.IsActive
                    && ((t.ParentTeamId is null && t.IsPublicPage)
                        || (t.ParentTeamId is not null && t.IsPromotedToDirectory)))
                .Select(t => CreateDirectorySummary(t, teamsById, userId))
                .ToList();

            return new TeamDirectoryResult(
                IsAuthenticated: false,
                CanCreateTeam: false,
                MyTeams: [],
                Departments: publicDepartments,
                SystemTeams: [],
                HiddenTeams: []);
        }

        var isBoardMember = await roleAssignmentService.IsUserBoardMemberAsync(userId.Value, cancellationToken);
        var isAdmin = await roleAssignmentService.IsUserAdminAsync(userId.Value, cancellationToken);
        var isTeamsAdmin = await roleAssignmentService.IsUserTeamsAdminAsync(userId.Value, cancellationToken);
        var canCreateTeam = isBoardMember || isAdmin || isTeamsAdmin;
        var canSeeHiddenTeams = canCreateTeam;

        var visibleTeams = canSeeHiddenTeams
            ? teamsById.Values
            : teamsById.Values.Where(t => !t.IsHidden);

        var directoryTeams = visibleTeams
            .Where(t => t.IsActive)
            .Where(t => t.ParentTeamId is null || t.IsPromotedToDirectory);

        var summaries = directoryTeams
            .Select(t => CreateDirectorySummary(t, teamsById, userId))
            .ToList();

        var myTeams = summaries
            .Where(t => t.IsCurrentUserMember)
            .ToList();

        var departments = summaries
            .Where(t => !t.IsCurrentUserMember && !t.IsSystemTeam && !t.IsHidden)
            .ToList();

        var systemTeams = summaries
            .Where(t => !t.IsCurrentUserMember && t.IsSystemTeam && !t.IsHidden)
            .ToList();

        var hiddenTeams = summaries
            .Where(t => !t.IsCurrentUserMember && t.IsHidden)
            .ToList();

        return new TeamDirectoryResult(
            IsAuthenticated: true,
            CanCreateTeam: canCreateTeam,
            MyTeams: myTeams,
            Departments: departments,
            SystemTeams: systemTeams,
            HiddenTeams: hiddenTeams);
    }

    private static TeamDirectorySummary CreateDirectorySummary(
        TeamInfo team,
        IReadOnlyDictionary<Guid, TeamInfo> teamById,
        Guid? userId)
    {
        TeamInfo? parent = team.ParentTeamId.HasValue && teamById.TryGetValue(team.ParentTeamId.Value, out var resolvedParent)
            ? resolvedParent
            : null;
        var isCurrentUserMember = userId.HasValue && team.Members.Any(m => m.UserId == userId.Value);
        var isCurrentUserCoordinator = userId.HasValue && team.Members.Any(m =>
            m.UserId == userId.Value &&
            m.Role == TeamMemberRole.Coordinator);

        return new TeamDirectorySummary(
            team.Id,
            team.Name,
            team.Description,
            team.Slug,
            team.Members.Count,
            team.IsSystemTeam,
            team.IsHidden,
            team.RequiresApproval,
            team.IsPublicPage,
            isCurrentUserMember,
            isCurrentUserCoordinator,
            parent?.Name,
            parent?.Slug);
    }
}
