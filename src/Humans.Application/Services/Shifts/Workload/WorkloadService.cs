using Humans.Application.DTOs.Shifts.Workload;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Shifts.Workload;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Services.Shifts.Workload;

/// <summary>
/// Workload aggregations for the coordinator dashboard. Reads via cached <see cref="IShiftView"/>;
/// no own cache (per-rota cache eviction already covers mutations).
/// </summary>
public sealed class WorkloadService(
    IShiftManagementRepository repo,
    IShiftView view,
    ITeamServiceRead teamService,
    IUserServiceRead userService) : IWorkloadService
{
    private static readonly decimal AllDayShiftHours = (decimal)Duration.FromTicks(
        Shift.AllDayWindowEnd.TickOfDay - Shift.AllDayWindowStart.TickOfDay).TotalHours;

    public async Task<WorkloadReport?> GetForActiveEventAsync(CancellationToken ct = default)
    {
        var es = await repo.GetActiveEventSettingsAsync(ct);
        if (es is null) return null;

        // Distinct rotaIds off the shared event-shift query avoids adding an interface method.
        var shiftStubs = await repo.GetEventShiftsAsync(new ShiftEventQuery(es.Id), ct);
        var rotaIds = shiftStubs.Select(s => s.RotaId).Distinct().ToList();
        if (rotaIds.Count == 0)
        {
            return new WorkloadReport(
                EventSettingsId: es.Id,
                EventYear: es.Year,
                ByPerson: [],
                ByRota: [],
                ByDepartment: []);
        }

        // ShiftRotaView is unfiltered — workload view is admin-only and needs hidden rotas.
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

        // Role estimates come from the cached team projection (definitions carry
        // EstimatedHours + RolePeriod; assignments carry the holder's user id).
        var rolePersonHours = BuildRolePersonHours(allTeams);
        var roleDeptHours = BuildRoleDeptHours(allTeams);

        var byRota = BuildByRota(entries, teamLookup);
        var byDepartment = BuildByDepartment(entries, teamLookup, roleDeptHours);
        var byPerson = await BuildByPersonAsync(entries, es, rolePersonHours, ct);

        return new WorkloadReport(
            EventSettingsId: es.Id,
            EventYear: es.Year,
            ByPerson: byPerson,
            ByRota: byRota,
            ByDepartment: byDepartment);
    }

    private static decimal HoursOf(Shift shift) =>
        shift.IsAllDay ? AllDayShiftHours : (decimal)shift.Duration.TotalHours;

    // Unsorted; controller assembles display order (display-sort-in-controllers).
    private static List<WorkloadByRotaRow> BuildByRota(
        IReadOnlyList<(Rota Rota, Shift Shift)> entries,
        IReadOnlyDictionary<Guid, TeamInfo> teamLookup) =>
        entries
            .GroupBy(e => e.Rota.Id)
            .Select(g =>
            {
                var rota = g.First().Rota;
                var hoursPerShift = g.ToDictionary(e => e.Shift.Id, e => HoursOf(e.Shift));
                var plannedSlots = g.Sum(e => e.Shift.MaxVolunteers);
                var filledSlots = g.Sum(e => Math.Min(
                    e.Shift.ShiftSignups.Count(ss => ss.Status == SignupStatus.Confirmed),
                    e.Shift.MaxVolunteers));
                var plannedHours = g.Sum(e => hoursPerShift[e.Shift.Id] * e.Shift.MaxVolunteers);
                var filledHours = g.Sum(e => hoursPerShift[e.Shift.Id] *
                    Math.Min(e.Shift.ShiftSignups.Count(ss => ss.Status == SignupStatus.Confirmed), e.Shift.MaxVolunteers));
                var pending = g.Sum(e => e.Shift.ShiftSignups.Count(ss => ss.Status == SignupStatus.Pending));
                var teamName = teamLookup.TryGetValue(rota.TeamId, out var team) ? team.Name : "(unknown)";
                return new WorkloadByRotaRow(
                    RotaId: rota.Id,
                    RotaName: rota.Name,
                    TeamId: rota.TeamId,
                    TeamName: teamName,
                    ShiftCount: g.Count(),
                    PlannedSlots: plannedSlots,
                    FilledSlots: filledSlots,
                    PendingSignupCount: pending,
                    PlannedHours: plannedHours,
                    FilledHours: filledHours);
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
                var hoursPerShift = g.ToDictionary(e => e.Shift.Id, e => HoursOf(e.Shift));
                var plannedSlots = g.Sum(e => e.Shift.MaxVolunteers);
                var filledSlots = g.Sum(e => Math.Min(
                    e.Shift.ShiftSignups.Count(ss => ss.Status == SignupStatus.Confirmed),
                    e.Shift.MaxVolunteers));
                var plannedHours = g.Sum(e => hoursPerShift[e.Shift.Id] * e.Shift.MaxVolunteers);
                var filledHours = g.Sum(e => hoursPerShift[e.Shift.Id] *
                    Math.Min(e.Shift.ShiftSignups.Count(ss => ss.Status == SignupStatus.Confirmed), e.Shift.MaxVolunteers));
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
                    PlannedSlots: plannedSlots,
                    FilledSlots: filledSlots,
                    PlannedHours: plannedHours,
                    FilledHours: filledHours);
            });

        // Fold role hours into the matching department; create rows for teams
        // that own roles but no rotas in this event.
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
        EventSettings es,
        IReadOnlyDictionary<Guid, RolePersonHours> rolePersonHours,
        CancellationToken ct)
    {
        // Confirmed → per-phase hours; Pending → count only (don't inflate
        // burnout from queued work).
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

        // Fold role estimates in; a role-only holder (no signups) still gets a row.
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

    // ── Role-hours aggregation off the cached TeamInfo projection ──────────────────

    private sealed record RolePersonHours(decimal YearRound, decimal Build, decimal Event, decimal Strike);

    private sealed record RoleDeptHours(string TeamName, string TeamSlug, decimal PlannedHours, decimal FilledHours);

    // Roles on deactivated teams are excluded — deactivation doesn't clear role
    // assignments, so a retired team's stale holders would otherwise leak in.
    private static IEnumerable<TeamRoleDefinitionSnapshot> ActiveRoles(
        IReadOnlyDictionary<Guid, TeamInfo> allTeams) =>
        allTeams.Values
            .Where(t => t.IsActive)
            .SelectMany(t => t.RoleDefinitions ?? []);

    // Per holder: each assigned user contributes the role's full annual estimate,
    // bucketed by the role's period (year-round in its own bucket).
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
                perUser[userId] = AddByPeriod(h, role.Period, est);
            }
        }
        return perUser;
    }

    // Per department: planned = estimate × slots, filled = estimate × assigned slots.
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

    private static RolePersonHours AddByPeriod(RolePersonHours h, RolePeriod period, int est) =>
        period switch
        {
            RolePeriod.Build => h with { Build = h.Build + est },
            RolePeriod.Event => h with { Event = h.Event + est },
            RolePeriod.Strike => h with { Strike = h.Strike + est },
            _ => h with { YearRound = h.YearRound + est },
        };
}
