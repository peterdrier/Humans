using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
using Humans.Shifts.Contracts;
using Humans.Base.Enums;
using Humans.Teams.Data;
using Humans.Teams.Domain;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Teams.Services;

internal sealed class TeamSnapshotReader(
    ITeamRepository repo,
    IServiceProvider serviceProvider,
    IShiftService shiftManagementService) : IApplicationService
{
    // Lazy: Users depends on Teams during service resolution.
    private IUserService UserService => serviceProvider.GetRequiredService<IUserService>();

    private ITeamResourceService TeamResourceService => serviceProvider.GetRequiredService<ITeamResourceService>();

    public async Task<AdminTeamListResult> GetAdminTeamListAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var (items, totalCount) = await repo.GetAllForAdminAsync(page, pageSize, cancellationToken);

        var pendingShiftCounts = await shiftManagementService
            .GetActivePendingShiftSignupCountsByTeamAsync(cancellationToken);

        var teamIds = items.Select(t => t.Id).ToList();
        var resourceSummaries = await TeamResourceService
            .GetTeamResourceSummariesAsync(teamIds, cancellationToken);

        return new AdminTeamListResult(
            BuildAdminTeamSummaries(items, pendingShiftCounts, resourceSummaries),
            totalCount);
    }

    private static IReadOnlyList<AdminTeamSummary> BuildAdminTeamSummaries(
        IReadOnlyList<Team> teams,
        IReadOnlyDictionary<Guid, int> pendingShiftCounts,
        IReadOnlyDictionary<Guid, TeamResourceSummary> resourceSummaries)
    {
        var ordered = new List<AdminTeamSummary>(teams.Count);

        foreach (var team in teams)
        {
            if (team.ParentTeamId.HasValue)
                continue;

            ordered.Add(CreateAdminTeamSummary(team, isChildTeam: false, pendingShiftCounts, resourceSummaries));

            var children = teams
                .Where(child => child.ParentTeamId == team.Id)
                .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase);

            ordered.AddRange(children.Select(child =>
                CreateAdminTeamSummary(child, isChildTeam: true, pendingShiftCounts, resourceSummaries)));
        }

        return ordered;
    }

    private static AdminTeamSummary CreateAdminTeamSummary(
        Team team,
        bool isChildTeam,
        IReadOnlyDictionary<Guid, int> pendingShiftCounts,
        IReadOnlyDictionary<Guid, TeamResourceSummary> resourceSummaries)
    {
        var systemTeamType = team.SystemTeamType != SystemTeamType.None
            ? team.SystemTeamType.ToString()
            : null;
        var resourceSummary = resourceSummaries.TryGetValue(team.Id, out var summary)
            ? summary
            : TeamResourceSummary.Empty;

        return new AdminTeamSummary(
            team.Id,
            team.Name,
            team.Slug,
            team.IsActive,
            team.RequiresApproval,
            team.IsSystemTeam,
            systemTeamType,
            team.Members.Count,
            team.JoinRequests.Count,
            resourceSummary.HasMailGroup,
            team.GoogleGroupEmail,
            resourceSummary.DriveResourceCount,
            team.RoleDefinitions.Sum(role => role.SlotCount),
            team.CreatedAt,
            isChildTeam,
            pendingShiftCounts.GetValueOrDefault(team.Id),
            team.IsHidden);
    }

    public async Task<IReadOnlyDictionary<Guid, TeamInfo>> ResolveTeamMembersAsync(
        IReadOnlyDictionary<Guid, TeamReadSnapshot> teams, CancellationToken ct = default)
    {
        var userIds = teams.Values.SelectMany(t => t.Members.Select(m => m.UserId)).Distinct().ToList();
        var users = userIds.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await UserService.GetUserInfosAsync(userIds, ct);
        return teams.ToDictionary(pair => pair.Key, pair => pair.Value.Team with
        {
            Members = pair.Value.Members.Select(member =>
            {
                users.TryGetValue(member.UserId, out var user);
                return new TeamMemberInfo(
                    member.TeamMemberId, member.UserId, user?.BurnerName ?? string.Empty,
                    user?.Email, user?.ProfilePictureUrl, member.Role, member.JoinedAt);
            }).ToList(),
        });
    }

    public async Task<IReadOnlyDictionary<Guid, TeamReadSnapshot>> GetTeamSnapshotsAsync(CancellationToken ct = default)
    {
        var teams = await repo.GetAllWithMembersAsync(ct);
        var managementHolders = await repo.GetActiveManagementRoleHolderUserIdsByTeamAsync(ct);
        var roleDefinitionsByTeam = await repo.GetAllRoleDefinitionsByTeamAsync(ct);
        var childIdsByParent = teams
            .Where(t => t.ParentTeamId.HasValue)
            .GroupBy(t => t.ParentTeamId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(t => t.Id).ToList());
        var pendingCounts = await repo.GetPendingCountsByTeamIdsAsync(
            teams.Select(t => t.Id).ToList(), ct);

        return teams.ToDictionary(
            t => t.Id,
            t => BuildTeamSnapshot(t, managementHolders, roleDefinitionsByTeam, childIdsByParent, pendingCounts));
    }

    private static TeamReadSnapshot BuildTeamSnapshot(
        Team team,
        IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> managementHolders,
        IReadOnlyDictionary<Guid, IReadOnlyList<TeamRoleDefinition>> roleDefinitionsByTeam,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> childIdsByParent,
        IReadOnlyDictionary<Guid, int> pendingCounts) => new(new TeamInfo(
        Id: team.Id,
        Name: team.Name,
        Description: team.Description,
        Slug: team.Slug,
        IsActive: team.IsActive,
        IsSystemTeam: team.IsSystemTeam,
        SystemTeamType: team.SystemTeamType,
        RequiresApproval: team.RequiresApproval,
        IsPublicPage: team.IsPublicPage,
        IsHidden: team.IsHidden,
        IsPromotedToDirectory: team.IsPromotedToDirectory,
        CreatedAt: team.CreatedAt,
        Members: [],
        ParentTeamId: team.ParentTeamId,
        GoogleGroupPrefix: team.GoogleGroupPrefix,
        HasBudget: team.HasBudget,
        IsSensitive: team.IsSensitive,
        UpdatedAt: team.UpdatedAt,
        CustomSlug: team.CustomSlug,
        ManagementRoleHolderUserIds: managementHolders.TryGetValue(team.Id, out var holders) ? holders : null,
        RoleDefinitions: roleDefinitionsByTeam.TryGetValue(team.Id, out var defs)
            ? defs.Select(d => ToRoleDefinitionSnapshot(d, team)).ToList()
            : null,
        ChildTeamIds: childIdsByParent.TryGetValue(team.Id, out var childIds) ? childIds : null,
        ShowCoordinatorsOnPublicPage: team.ShowCoordinatorsOnPublicPage,
        PageContent: team.PageContent,
        CallsToAction: team.CallsToAction,
        PageContentUpdatedAt: team.PageContentUpdatedAt,
        PageContentUpdatedByUserId: team.PageContentUpdatedByUserId,
        PendingRequestCount: pendingCounts.TryGetValue(team.Id, out var pending) ? pending : 0,
        EarlyEntryEnabled: team.EarlyEntryEnabled),
        team.Members.Where(m => m.LeftAt is null)
            .Select(m => new TeamMemberReference(m.Id, m.UserId, m.Role, m.JoinedAt)).ToList());

    internal static TeamRoleDefinitionSnapshot ToRoleDefinitionSnapshot(
        TeamRoleDefinition definition,
        Team? fallbackTeam = null) =>
        new(
            definition.Id,
            definition.TeamId,
            definition.Team?.Name ?? fallbackTeam?.Name ?? string.Empty,
            definition.Team?.Slug ?? fallbackTeam?.Slug ?? string.Empty,
            definition.Name,
            definition.Description,
            definition.SlotCount,
            definition.EstimatedHours,
            definition.Priorities,
            definition.SortOrder,
            definition.IsManagement,
            definition.Period,
            definition.IsPublic,
            definition.Assignments
                .Select(assignment => new TeamRoleAssignmentSnapshot(
                    assignment.Id,
                    assignment.TeamMemberId,
                    assignment.SlotIndex,
                    assignment.TeamMember?.UserId))
                .ToList());
}
