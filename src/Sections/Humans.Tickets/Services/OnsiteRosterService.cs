using Humans.Auth.Contracts;
using Humans.Camps.Contracts;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;

namespace Humans.Tickets.Services;

internal sealed class OnsiteRosterService : IOnsiteRosterService
{
    private readonly IEventParticipationManagement _participations;
    private readonly ICampService _camps;
    private readonly ITeamService _teams;
    private readonly IRoleAssignmentService _roles;

    public OnsiteRosterService(
        IEventParticipationManagement participations,
        ICampService camps,
        ITeamService teams,
        IRoleAssignmentService roles)
    {
        _participations = participations;
        _camps = camps;
        _teams = teams;
        _roles = roles;
    }

    public async Task<OnsiteRosterResult> GetRosterAsync(
        int year,
        string? campFilter,
        string? teamFilter,
        string? roleFilter,
        CancellationToken ct = default)
    {
        if (year <= 0)
            return new OnsiteRosterResult([], [], [], []);

        var onsite = (await _participations.GetAllParticipationsAsync(ct))
            .Where(p => p.Year == year && p.OnsiteSince is not null)
            .ToList();
        if (onsite.Count == 0)
            return new OnsiteRosterResult([], [], [], []);

        var onsiteIds = onsite.Select(o => o.UserId).ToHashSet();

        var campNamesByUserId = await BuildCampNamesAsync(year, onsiteIds, ct);
        var teamNamesByUserId = await BuildTeamNamesAsync(onsiteIds, ct);
        var roleNamesByUserId = await BuildRoleNamesAsync(onsiteIds, ct);

        IEnumerable<(EventParticipationInfo Row, IReadOnlyList<string> Camps,
            IReadOnlyList<string> Teams, IReadOnlyList<string> Roles)> joined = onsite
            .Select(o => (
                Row: o,
                Camps: NamesFor(campNamesByUserId, o.UserId),
                Teams: NamesFor(teamNamesByUserId, o.UserId),
                Roles: NamesFor(roleNamesByUserId, o.UserId)));

        if (!string.IsNullOrWhiteSpace(campFilter))
            joined = joined.Where(x => x.Camps.Contains(campFilter, StringComparer.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(teamFilter))
            joined = joined.Where(x => x.Teams.Contains(teamFilter, StringComparer.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(roleFilter))
            joined = joined.Where(x => x.Roles.Contains(roleFilter, StringComparer.OrdinalIgnoreCase));

        var rows = joined
            .Select(x => new OnsiteRosterRow(
                x.Row.UserId,
                x.Row.OnsiteSince!.Value,
                x.Camps,
                x.Teams,
                x.Roles))
            .ToList();

        var availableCamps = DistinctSortedNames(campNamesByUserId);
        var availableTeams = DistinctSortedNames(teamNamesByUserId);
        var availableRoles = DistinctSortedNames(roleNamesByUserId);

        return new OnsiteRosterResult(rows, availableCamps, availableTeams, availableRoles);
    }

    private async Task<Dictionary<Guid, SortedSet<string>>> BuildCampNamesAsync(
        int year, HashSet<Guid> onsiteIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, SortedSet<string>>();
        var camps = await _camps.GetCampsForYearAsync(year, ct);
        foreach (var camp in camps)
        {
            foreach (var season in camp.Seasons)
            {
                foreach (var m in season.Members)
                {
                    if (!onsiteIds.Contains(m.UserId)) continue;
                    if (m.Status != CampMemberStatus.Active) continue;
                    Add(result, m.UserId, season.Name);
                }
            }
        }
        return result;
    }

    private async Task<Dictionary<Guid, SortedSet<string>>> BuildTeamNamesAsync(
        HashSet<Guid> onsiteIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, SortedSet<string>>();
        var teamsById = await _teams.GetTeamsAsync(ct);
        foreach (var (_, team) in teamsById)
        {
            if (!team.IsActive) continue;
            if (team.IsSystemTeam) continue;
            foreach (var member in team.Members)
            {
                if (!onsiteIds.Contains(member.UserId)) continue;
                Add(result, member.UserId, team.Name);
            }
        }
        return result;
    }

    private async Task<Dictionary<Guid, SortedSet<string>>> BuildRoleNamesAsync(
        HashSet<Guid> onsiteIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, SortedSet<string>>();
        foreach (var userId in onsiteIds)
        {
            var assignments = await _roles.GetActiveForUserAsync(userId, ct);
            if (assignments.Count == 0) continue;
            foreach (var r in assignments) Add(result, userId, r.RoleName);
        }
        return result;
    }

    private static void Add(Dictionary<Guid, SortedSet<string>> map, Guid userId, string name)
    {
        if (!map.TryGetValue(userId, out var set))
        {
            set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            map[userId] = set;
        }
        set.Add(name);
    }

    private static IReadOnlyList<string> NamesFor(
        Dictionary<Guid, SortedSet<string>> map, Guid userId) =>
        map.TryGetValue(userId, out var set) ? set.ToList() : [];

    private static IReadOnlyList<string> DistinctSortedNames(
        Dictionary<Guid, SortedSet<string>> map) =>
        map.Values
            .SelectMany(s => s)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
