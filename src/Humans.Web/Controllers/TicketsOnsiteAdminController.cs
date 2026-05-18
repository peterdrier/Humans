using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models.Tickets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>
/// "Who's onsite" view (#736). Flat list of every user with an Attended +
/// non-null CheckedInAt EventParticipation for the active event year, joined
/// with camp / team / governance-role names for filtering. Read-only.
/// </summary>
[Authorize(Policy = PolicyNames.TicketAdminBoardOrAdmin)]
[Route("Tickets/Admin/Onsite")]
public sealed class TicketsOnsiteAdminController : HumansControllerBase
{
    private readonly IShiftManagementService _shifts;
    private readonly ICampService _camps;
    private readonly ITeamService _teams;
    private readonly IRoleAssignmentService _roles;

    public TicketsOnsiteAdminController(
        IUserService userService,
        IShiftManagementService shifts,
        ICampService camps,
        ITeamService teams,
        IRoleAssignmentService roles)
        : base(userService)
    {
        _shifts = shifts;
        _camps = camps;
        _teams = teams;
        _roles = roles;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        [FromQuery] string? camp,
        [FromQuery] string? team,
        [FromQuery] string? role,
        CancellationToken ct)
    {
        var active = await _shifts.GetActiveAsync();
        if (active is null || active.Year == 0)
        {
            // No active event configured — empty roster.
            return View("~/Views/Tickets/Admin/Onsite.cshtml",
                new OnsiteRosterViewModel(
                    Year: 0,
                    CampFilter: camp,
                    TeamFilter: team,
                    RoleFilter: role,
                    AvailableCamps: [],
                    AvailableTeams: [],
                    AvailableRoles: [],
                    Rows: []));
        }

        var year = active.Year;

        var onsite = await UserService.GetOnsiteUsersAsync(year, ct);
        var onsiteIds = onsite.Select(o => o.UserId).ToHashSet();

        // ---- Build per-user maps for camp / team / role names ----

        var camps = await _camps.GetCampsForYearAsync(year, ct);
        var campNamesByUserId = new Dictionary<Guid, SortedSet<string>>();
        foreach (var campInfo in camps)
        {
            // CampInfo.Seasons is filtered to this year by GetCampsForYearAsync.
            foreach (var season in campInfo.Seasons)
            {
                var members = await _camps.GetSeasonMembersAsync(season.Id, ct);
                foreach (var m in members)
                {
                    if (!onsiteIds.Contains(m.UserId)) continue;
                    if (m.Status != CampMemberStatus.Active) continue;
                    if (!campNamesByUserId.TryGetValue(m.UserId, out var set))
                    {
                        set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                        campNamesByUserId[m.UserId] = set;
                    }
                    set.Add(season.Name);
                }
            }
        }

        var teamsById = await _teams.GetTeamsAsync(ct);
        var teamNamesByUserId = new Dictionary<Guid, SortedSet<string>>();
        foreach (var (_, teamInfo) in teamsById)
        {
            if (!teamInfo.IsActive) continue;
            if (teamInfo.IsSystemTeam) continue; // system teams (Volunteers etc) aren't a useful filter here
            foreach (var member in teamInfo.Members)
            {
                if (!onsiteIds.Contains(member.UserId)) continue;
                if (!teamNamesByUserId.TryGetValue(member.UserId, out var set))
                {
                    set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    teamNamesByUserId[member.UserId] = set;
                }
                set.Add(teamInfo.Name);
            }
        }

        var roleNamesByUserId = new Dictionary<Guid, SortedSet<string>>();
        foreach (var userId in onsiteIds)
        {
            var rs = await _roles.GetActiveForUserAsync(userId, ct);
            if (rs.Count == 0) continue;
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rs) set.Add(r.RoleName);
            roleNamesByUserId[userId] = set;
        }

        // ---- Filter ----

        var filtered = onsite.Select(o =>
        {
            var campsForUser = campNamesByUserId.TryGetValue(o.UserId, out var c)
                ? (IReadOnlyList<string>)c.ToList()
                : [];
            var teamsForUser = teamNamesByUserId.TryGetValue(o.UserId, out var t)
                ? (IReadOnlyList<string>)t.ToList()
                : [];
            var rolesForUser = roleNamesByUserId.TryGetValue(o.UserId, out var r)
                ? (IReadOnlyList<string>)r.ToList()
                : [];
            return (Row: o, Camps: campsForUser, Teams: teamsForUser, Roles: rolesForUser);
        });

        if (!string.IsNullOrWhiteSpace(camp))
            filtered = filtered.Where(x => x.Camps.Contains(camp!, StringComparer.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(team))
            filtered = filtered.Where(x => x.Teams.Contains(team!, StringComparer.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(role))
            filtered = filtered.Where(x => x.Roles.Contains(role!, StringComparer.OrdinalIgnoreCase));

        // Display sort: most-recent check-in first.
        var rows = filtered
            .Where(x => x.Row.CheckedInAt is not null)
            .OrderByDescending(x => x.Row.CheckedInAt)
            .Select(x => new OnsiteRosterRow(
                x.Row.UserId,
                x.Row.DisplayName,
                x.Row.CheckedInAt!.Value,
                x.Camps,
                x.Teams,
                x.Roles))
            .ToList();

        var availableCamps = campNamesByUserId.Values
            .SelectMany(s => s)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var availableTeams = teamNamesByUserId.Values
            .SelectMany(s => s)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var availableRoles = roleNamesByUserId.Values
            .SelectMany(s => s)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return View("~/Views/Tickets/Admin/Onsite.cshtml",
            new OnsiteRosterViewModel(
                Year: year,
                CampFilter: camp,
                TeamFilter: team,
                RoleFilter: role,
                AvailableCamps: availableCamps,
                AvailableTeams: availableTeams,
                AvailableRoles: availableRoles,
                Rows: rows));
    }
}
