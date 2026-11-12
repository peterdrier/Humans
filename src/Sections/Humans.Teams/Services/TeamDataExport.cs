using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Domain;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Teams.Services;

internal sealed class TeamDataExport(ITeamRepository repo, IServiceProvider serviceProvider) : IApplicationService
{
    internal const string TeamMemberships = "TeamMemberships";
    internal const string TeamJoinRequests = "TeamJoinRequests";
    internal const string TeamEarlyEntry = "TeamEarlyEntry";

    // Lazy: Users depends on Teams during service resolution.
    private IUserService UserService => serviceProvider.GetRequiredService<IUserService>();

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var info = await UserService.GetUserInfoAsync(userId, ct);
        List<TeamMember> memberships = [];
        List<TeamJoinRequest> joinRequests = [];
        List<TeamEarlyEntryGrant> eeGrants = [];
        foreach (var id in info?.AllUserIds ?? [userId])
        {
            memberships.AddRange(await repo.GetAllMembershipsForUserAsync(id, ct));
            joinRequests.AddRange(await repo.GetAllJoinRequestsForUserAsync(id, ct));
            eeGrants.AddRange(await repo.GetEarlyEntryGrantsForUserAsync(id, ct));
        }
        var teamIds = memberships.Select(tm => tm.TeamId)
            .Concat(joinRequests.Select(tjr => tjr.TeamId))
            .Concat(eeGrants.Select(g => g.TeamId))
            .Distinct()
            .ToList();
        var teamsById = await repo.GetByIdsWithParentsAsync(teamIds, ct);

        string GetTeamName(Guid teamId) =>
            teamsById.TryGetValue(teamId, out var team) ? team.Name : string.Empty;

        var membershipSlice = new UserDataSlice(TeamMemberships, memberships.Select(tm => new
        {
            TeamName = GetTeamName(tm.TeamId),
            tm.Role,
            JoinedAt = tm.JoinedAt.ToIso8601(),
            LeftAt = tm.LeftAt.ToIso8601(),
            TeamRoles = tm.RoleAssignments.Select(tra => new
            {
                RoleName = tra.TeamRoleDefinition.Name,
                AssignedAt = tra.AssignedAt.ToIso8601()
            })
        }).ToList());

        var joinRequestSlice = new UserDataSlice(TeamJoinRequests, joinRequests.Select(tjr => new
        {
            TeamName = GetTeamName(tjr.TeamId),
            tjr.Status,
            tjr.Message,
            tjr.ReviewNotes,
            RequestedAt = tjr.RequestedAt.ToIso8601(),
            ResolvedAt = tjr.ResolvedAt.ToIso8601(),
            StateHistory = tjr.StateHistory.OrderBy(h => h.ChangedAt).Select(h => new
            {
                h.Status,
                ChangedAt = h.ChangedAt.ToIso8601(),
                h.Notes
            }).ToList()
        }).ToList());

        var earlyEntrySlice = new UserDataSlice(TeamEarlyEntry, eeGrants.Select(g => new
        {
            g.EventSettingsId,
            TeamName = GetTeamName(g.TeamId),
            g.ProjectName,
            EntryDate = g.EntryDate.ToInvariantDate(),
            GrantedAt = g.CreatedAt.ToIso8601(),
        }).ToList());

        return [membershipSlice, joinRequestSlice, earlyEntrySlice];
    }
}
