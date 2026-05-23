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
    ITeamService teamService,
    IUserServiceRead userService) : IWorkloadService
{
    private static readonly decimal AllDayShiftHours = (decimal)Duration.FromTicks(
        Shift.AllDayWindowEnd.TickOfDay - Shift.AllDayWindowStart.TickOfDay).TotalHours;

    public async Task<WorkloadReport?> GetForActiveEventAsync(CancellationToken ct = default)
    {
        var es = await repo.GetActiveEventSettingsAsync(ct);
        if (es is null) return null;

        // Distinct rotaIds off GetShiftsForEventAsync — avoids adding an interface method.
        var shiftStubs = await repo.GetShiftsForEventAsync(es.Id, null, ct);
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

        var teamIds = entries.Select(e => e.Rota.TeamId).Distinct().ToList();
        var teamLookup = teamIds.Count > 0
            ? await teamService.GetByIdsWithParentsAsync(teamIds, ct)
            : new Dictionary<Guid, Team>();

        var byRota = BuildByRota(entries, teamLookup);
        var byDepartment = BuildByDepartment(entries, teamLookup);
        var byPerson = await BuildByPersonAsync(entries, es, ct);

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
        IReadOnlyDictionary<Guid, Team> teamLookup) =>
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
        IReadOnlyDictionary<Guid, Team> teamLookup) =>
        entries
            .GroupBy(e => e.Rota.TeamId)
            .Select(g =>
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
            })
            .ToList();

    private async Task<List<WorkloadByPersonRow>> BuildByPersonAsync(
        IReadOnlyList<(Rota Rota, Shift Shift)> entries,
        EventSettings es,
        CancellationToken ct)
    {
        // Confirmed → per-phase hours; Pending → count only (don't inflate
        // burnout from queued work).
        var perUser = new Dictionary<Guid, (int Confirmed, int Pending, decimal Build, decimal Event, decimal Strike)>();
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
                        ShiftPeriod.Build => (totals.Confirmed + 1, totals.Pending, totals.Build + hours, totals.Event, totals.Strike),
                        ShiftPeriod.Event => (totals.Confirmed + 1, totals.Pending, totals.Build, totals.Event + hours, totals.Strike),
                        _ => (totals.Confirmed + 1, totals.Pending, totals.Build, totals.Event, totals.Strike + hours),
                    };
                }
                else
                {
                    totals = (totals.Confirmed, totals.Pending + 1, totals.Build, totals.Event, totals.Strike);
                }
                perUser[signup.UserId] = totals;
            }
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
                    BuildHours: kvp.Value.Build,
                    EventHours: kvp.Value.Event,
                    StrikeHours: kvp.Value.Strike);
            })
            .ToList();
    }
}
