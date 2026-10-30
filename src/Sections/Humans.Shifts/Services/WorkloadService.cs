using Humans.Shifts.Services.Dtos;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Shifts.Domain;
using NodaTime;
using Humans.Shifts.Data;
using Humans.Users.Contracts;

namespace Humans.Shifts.Services;

/// <summary>No cache of its own: reads go through the cached row view, whose eviction covers mutations.</summary>
internal sealed class WorkloadService(
    IShiftManagementRepository repo,
    IShiftRowView view,
    ITeamService teamService,
    IUserService userService,
    EventCalendarResolver calendarResolver) : IWorkloadService
{
    private static readonly decimal AllDayShiftHours = (decimal)Duration.FromTicks(
        Shift.AllDayWindowEnd.TickOfDay - Shift.AllDayWindowStart.TickOfDay).TotalHours;

    public async Task<WorkloadReport?> GetForActiveEventAsync(CancellationToken ct = default)
    {
        var calendar = await calendarResolver.GetActiveAsync(ct);
        if (calendar is null) return null;

        // Reuses the event-shift query for the rota ids rather than add an interface method.
        var shiftStubs = await repo.GetEventShiftsAsync(new ShiftEventQuery(calendar.Id), ct);
        var rotaIds = shiftStubs.Select(s => s.RotaId).Distinct().ToList();
        // Unfiltered: the workload view is admin-only and needs hidden rotas.
        var views = await view.GetRotasAsync(rotaIds, ct).ConfigureAwait(false);
        var entries = views.Values
            .Where(v => v.Rota is not null)
            .SelectMany(v => v.Shifts.Select(s => (Rota: v.Rota!, Shift: s)))
            .ToList();

        var allTeams = await teamService.GetTeamsAsync(ct);
        var teamLookup = entries
            .Select(e => e.Rota.TeamId)
            .Distinct()
            .Where(allTeams.ContainsKey)
            .ToDictionary(id => id, id => allTeams[id]);

        var rolePersonHours = BuildRolePersonHours(allTeams);
        var roleDeptHours = BuildRoleDeptHours(allTeams);

        var byRota = BuildByRota(entries, teamLookup);
        var byDepartment = BuildByDepartment(entries, teamLookup, roleDeptHours);
        var byPerson = await BuildByPersonAsync(entries, calendar, rolePersonHours, ct);

        return new WorkloadReport(
            EventSettingsId: calendar.Id,
            EventYear: calendar.Year,
            ByPerson: byPerson,
            ByRota: byRota,
            ByDepartment: byDepartment);
    }

    private static decimal HoursOf(Shift shift) =>
        shift.IsAllDay ? AllDayShiftHours : (decimal)shift.Duration.TotalHours;

    private static (int PlannedSlots, int FilledSlots, int Pending, decimal PlannedHours, decimal FilledHours)
        SumShiftWorkload(IEnumerable<Shift> shifts)
    {
        (int PlannedSlots, int FilledSlots, int Pending, decimal PlannedHours, decimal FilledHours) totals = default;
        checked
        {
            foreach (var shift in shifts)
            {
                var hours = HoursOf(shift);
                var filled = Math.Min(shift.ShiftSignups.Count(s => s.Status == SignupStatus.Confirmed), shift.MaxVolunteers);
                totals.PlannedSlots += shift.MaxVolunteers;
                totals.FilledSlots += filled;
                totals.Pending += shift.ShiftSignups.Count(s => s.Status == SignupStatus.Pending);
                totals.PlannedHours += hours * shift.MaxVolunteers;
                totals.FilledHours += hours * filled;
            }
        }
        return totals;
    }

    // Unsorted; the controller orders for display.
    private static List<WorkloadByRotaRow> BuildByRota(
        IReadOnlyList<(Rota Rota, Shift Shift)> entries,
        IReadOnlyDictionary<Guid, TeamInfo> teamLookup) =>
        entries
            .GroupBy(e => e.Rota.Id)
            .Select(g =>
            {
                var rota = g.First().Rota;
                var totals = SumShiftWorkload(g.Select(e => e.Shift));
                var teamName = teamLookup.TryGetValue(rota.TeamId, out var team) ? team.Name : "(unknown)";
                return new WorkloadByRotaRow(
                    RotaId: rota.Id,
                    RotaName: rota.Name,
                    TeamId: rota.TeamId,
                    TeamName: teamName,
                    ShiftCount: g.Count(),
                    PlannedSlots: totals.PlannedSlots,
                    FilledSlots: totals.FilledSlots,
                    PendingSignupCount: totals.Pending,
                    PlannedHours: totals.PlannedHours,
                    FilledHours: totals.FilledHours);
            })
            .ToList();

    private static List<WorkloadByDepartmentRow> BuildByDepartment(
        IReadOnlyList<(Rota Rota, Shift Shift)> entries,
        IReadOnlyDictionary<Guid, TeamInfo> teamLookup,
        IReadOnlyDictionary<Guid, RoleDeptHours> roleDeptHours)
    {
        var rows = entries
            .GroupBy(e => e.Rota.TeamId)
            .ToDictionary(g => g.Key, g =>
            {
                var totals = SumShiftWorkload(g.Select(e => e.Shift));
                var (teamName, teamSlug) = teamLookup.TryGetValue(g.Key, out var team)
                    ? (team.Name, team.Slug)
                    : ("(unknown)", string.Empty);
                var rotaCount = g.Select(e => e.Rota.Id).Distinct().Count();
                return new WorkloadByDepartmentRow(
                    TeamId: g.Key,
                    TeamName: teamName,
                    TeamSlug: teamSlug,
                    RotaCount: rotaCount,
                    ShiftCount: g.Count(),
                    PlannedSlots: totals.PlannedSlots,
                    FilledSlots: totals.FilledSlots,
                    PlannedHours: totals.PlannedHours,
                    FilledHours: totals.FilledHours);
            });

        // Teams that own roles but no rotas in this event still get a row.
        foreach (var (teamId, role) in roleDeptHours)
        {
            rows[teamId] = rows.TryGetValue(teamId, out var existing)
                ? existing with
                {
                    PlannedHours = existing.PlannedHours + role.PlannedHours,
                    FilledHours = existing.FilledHours + role.FilledHours,
                }
                : new WorkloadByDepartmentRow(
                    TeamId: teamId,
                    TeamName: role.TeamName,
                    TeamSlug: role.TeamSlug,
                    RotaCount: 0,
                    ShiftCount: 0,
                    PlannedSlots: 0,
                    FilledSlots: 0,
                    PlannedHours: role.PlannedHours,
                    FilledHours: role.FilledHours);
        }

        return rows.Values.ToList();
    }

    private async Task<List<WorkloadByPersonRow>> BuildByPersonAsync(
        IReadOnlyList<(Rota Rota, Shift Shift)> entries,
        Humans.Settings.Contracts.EventSettingsInfo es,
        IReadOnlyDictionary<Guid, RolePersonHours> rolePersonHours,
        CancellationToken ct)
    {
        // Pending is a count only, so queued work doesn't inflate burnout.
        var perUser = new Dictionary<Guid, (int Confirmed, int Pending, decimal YearRound, decimal Build, decimal Event, decimal Strike)>();
        foreach (var (_, shift) in entries)
        {
            var hours = HoursOf(shift);
            var period = shift.GetShiftPeriod(es);
            foreach (var signup in shift.ShiftSignups)
            {
                if (signup.Status is not (SignupStatus.Confirmed or SignupStatus.Pending))
                    continue;

                perUser.TryGetValue(signup.UserId, out var totals);
                if (signup.Status == SignupStatus.Confirmed)
                {
                    totals = period switch
                    {
                        ShiftPeriod.Build => totals with { Confirmed = totals.Confirmed + 1, Build = totals.Build + hours },
                        ShiftPeriod.Event => totals with { Confirmed = totals.Confirmed + 1, Event = totals.Event + hours },
                        _ => totals with { Confirmed = totals.Confirmed + 1, Strike = totals.Strike + hours },
                    };
                }
                else
                {
                    totals = totals with { Pending = totals.Pending + 1 };
                }
                perUser[signup.UserId] = totals;
            }
        }

        // A role-only holder, with no signups, still gets a row.
        foreach (var (userId, role) in rolePersonHours)
        {
            perUser.TryGetValue(userId, out var totals);
            perUser[userId] = totals with
            {
                YearRound = totals.YearRound + role.YearRound,
                Build = totals.Build + role.Build,
                Event = totals.Event + role.Event,
                Strike = totals.Strike + role.Strike,
            };
        }

        if (perUser.Count == 0) return new List<WorkloadByPersonRow>();

        var users = await userService.GetUserInfosAsync(perUser.Keys.ToList(), ct);

        return perUser
            .Select(kvp =>
            {
                var name = users.TryGetValue(kvp.Key, out var user)
                    ? (!string.IsNullOrWhiteSpace(user.BurnerName) ? user.BurnerName : "(no name)")
                    : "(unknown user)";
                return new WorkloadByPersonRow(
                    UserId: kvp.Key,
                    DisplayName: name,
                    ConfirmedSignupCount: kvp.Value.Confirmed,
                    PendingSignupCount: kvp.Value.Pending,
                    YearRoundHours: kvp.Value.YearRound,
                    BuildHours: kvp.Value.Build,
                    EventHours: kvp.Value.Event,
                    StrikeHours: kvp.Value.Strike);
            })
            .ToList();
    }

    private sealed record RolePersonHours(decimal YearRound, decimal Build, decimal Event, decimal Strike);

    private sealed record RoleDeptHours(string TeamName, string TeamSlug, decimal PlannedHours, decimal FilledHours);

    // Deactivation doesn't clear role assignments, so a retired team's stale holders would otherwise leak in.
    private static IEnumerable<TeamRoleDefinitionSnapshot> ActiveRoles(
        IReadOnlyDictionary<Guid, TeamInfo> allTeams) =>
        allTeams.Values
            .Where(t => t.IsActive)
            .SelectMany(t => t.RoleDefinitions ?? []);

    // Each holder gets the role's full annual estimate.
    private static Dictionary<Guid, RolePersonHours> BuildRolePersonHours(
        IReadOnlyDictionary<Guid, TeamInfo> allTeams)
    {
        var perUser = new Dictionary<Guid, RolePersonHours>();
        foreach (var role in ActiveRoles(allTeams))
        {
            if (role.EstimatedHours is not { } est) continue;
            foreach (var assignment in role.Assignments)
            {
                if (assignment.AssignedUserId is not { } userId) continue;
                perUser.TryGetValue(userId, out var h);
                h ??= new RolePersonHours(0, 0, 0, 0);
                perUser[userId] = role.Period switch
                {
                    RolePeriod.Build => h with { Build = h.Build + est },
                    RolePeriod.Event => h with { Event = h.Event + est },
                    RolePeriod.Strike => h with { Strike = h.Strike + est },
                    _ => h with { YearRound = h.YearRound + est },
                };
            }
        }
        return perUser;
    }

    private static Dictionary<Guid, RoleDeptHours> BuildRoleDeptHours(
        IReadOnlyDictionary<Guid, TeamInfo> allTeams)
    {
        var perTeam = new Dictionary<Guid, RoleDeptHours>();
        foreach (var role in ActiveRoles(allTeams))
        {
            if (role.EstimatedHours is not { } est) continue;
            var assigned = role.Assignments.Count(a => a.AssignedUserId.HasValue);
            perTeam.TryGetValue(role.TeamId, out var h);
            h ??= new RoleDeptHours(role.TeamName, role.TeamSlug, 0, 0);
            perTeam[role.TeamId] = h with
            {
                PlannedHours = h.PlannedHours + est * role.SlotCount,
                FilledHours = h.FilledHours + est * assigned,
            };
        }
        return perTeam;
    }
}
