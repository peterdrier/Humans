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
/// Shifts-domain workload aggregations for the coordinator workload dashboard.
/// </summary>
/// <remarks>
/// <para>
/// Reads per-rota shift + signup rows through <see cref="IShiftView"/> — the
/// Shifts-section per-rota cache owned by <c>CachingShiftViewService</c>
/// (§15 Option B at the section level). The set of rotas to walk is derived
/// from <see cref="IShiftManagementRepository.GetShiftsForEventAsync"/> (no
/// signup payload, no new interface method). Cross-section name stitching
/// via <see cref="ITeamService"/> / <see cref="IUserService"/>.
/// </para>
/// <para>
/// This service does not hold its own cache: signup / shift / rota mutations
/// already evict the per-rota cache entries via
/// <see cref="IShiftViewInvalidator"/>, and the aggregation itself is
/// microsecond-scale CPU work over a few hundred rotas at our ~500-user scale.
/// Avoids a parallel cache key with its own invalidation path.
/// </para>
/// </remarks>
public sealed class WorkloadService : IWorkloadService
{
    private static readonly decimal AllDayShiftHours = (decimal)Duration.FromTicks(
        Shift.AllDayWindowEnd.TickOfDay - Shift.AllDayWindowStart.TickOfDay).TotalHours;

    private readonly IShiftManagementRepository _repo;
    private readonly IShiftView _view;
    private readonly ITeamService _teamService;
    private readonly IUserService _userService;

    public WorkloadService(
        IShiftManagementRepository repo,
        IShiftView view,
        ITeamService teamService,
        IUserService userService)
    {
        _repo = repo;
        _view = view;
        _teamService = teamService;
        _userService = userService;
    }

    public async Task<WorkloadReport?> GetForActiveEventAsync(CancellationToken ct = default)
    {
        var es = await _repo.GetActiveEventSettingsAsync(ct);
        if (es is null) return null;

        // Derive rota ids by inlining a distinct on the existing
        // GetShiftsForEventAsync (Shifts + Rota nav, no signups) — avoids
        // adding a new interface method
        // (memory/architecture/interface-method-additions-are-debt.md).
        var shiftStubs = await _repo.GetShiftsForEventAsync(es.Id, null, ct);
        var rotaIds = shiftStubs.Select(s => s.RotaId).Distinct().ToList();
        if (rotaIds.Count == 0)
        {
            return new WorkloadReport(
                EventSettingsId: es.Id,
                EventYear: es.Year,
                ByPerson: [],
                ByShift: [],
                ByDepartment: []);
        }

        // Per-rota cache supplies Rota + Shifts + ShiftSignups. AdminOnly
        // shifts and hidden rotas are INCLUDED — ShiftRotaView is unfiltered
        // and the workload view is admin-only (coordinators need full
        // visibility for balancing).
        var views = await _view.GetRotasAsync(rotaIds, ct).ConfigureAwait(false);
        var entries = views.Values
            .Where(v => v.Rota is not null)
            .SelectMany(v => v.Shifts.Select(s => (Rota: v.Rota!, Shift: s)))
            .ToList();

        var teamIds = entries.Select(e => e.Rota.TeamId).Distinct().ToList();
        var teamLookup = teamIds.Count > 0
            ? await _teamService.GetByIdsWithParentsAsync(teamIds, ct)
            : new Dictionary<Guid, Team>();

        var byShift = BuildByShift(entries, es, teamLookup);
        var byDepartment = BuildByDepartment(entries, teamLookup);
        var byPerson = await BuildByPersonAsync(entries, ct);

        return new WorkloadReport(
            EventSettingsId: es.Id,
            EventYear: es.Year,
            ByPerson: byPerson,
            ByShift: byShift,
            ByDepartment: byDepartment);
    }

    private static decimal HoursOf(Shift shift) =>
        shift.IsAllDay ? AllDayShiftHours : (decimal)shift.Duration.TotalHours;

    // Lists are returned unsorted; the controller assembles the display order
    // (memory/architecture/display-sort-in-controllers.md).
    private static List<WorkloadByShiftRow> BuildByShift(
        IReadOnlyList<(Rota Rota, Shift Shift)> entries,
        EventSettings es,
        IReadOnlyDictionary<Guid, Team> teamLookup) =>
        entries
            .Select(e =>
            {
                var s = e.Shift;
                var confirmed = s.ShiftSignups.Count(ss => ss.Status == SignupStatus.Confirmed);
                var pending = s.ShiftSignups.Count(ss => ss.Status == SignupStatus.Pending);
                var teamName = teamLookup.TryGetValue(e.Rota.TeamId, out var team) ? team.Name : "(unknown)";
                return new WorkloadByShiftRow(
                    ShiftId: s.Id,
                    RotaId: e.Rota.Id,
                    RotaName: e.Rota.Name,
                    TeamId: e.Rota.TeamId,
                    TeamName: teamName,
                    DayOffset: s.DayOffset,
                    Date: es.GateOpeningDate.PlusDays(s.DayOffset),
                    IsAllDay: s.IsAllDay,
                    StartTime: s.IsAllDay ? Shift.AllDayWindowStart : s.StartTime,
                    DurationHours: HoursOf(s),
                    MaxVolunteers: s.MaxVolunteers,
                    ConfirmedCount: confirmed,
                    PendingCount: pending);
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
                var teamName = teamLookup.TryGetValue(g.Key, out var team) ? team.Name : "(unknown)";
                var rotaCount = g.Select(e => e.Rota.Id).Distinct().Count();
                return new WorkloadByDepartmentRow(
                    TeamId: g.Key,
                    TeamName: teamName,
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
        CancellationToken ct)
    {
        // Walk every signup once. Confirmed contributes hours; Pending bumps the
        // pending count only (don't inflate burnout signal from queued work).
        var perUser = new Dictionary<Guid, (int Confirmed, int Pending, decimal Hours)>();
        foreach (var (_, shift) in entries)
        {
            var hours = HoursOf(shift);
            foreach (var signup in shift.ShiftSignups)
            {
                if (signup.Status is not (SignupStatus.Confirmed or SignupStatus.Pending))
                    continue;

                perUser.TryGetValue(signup.UserId, out var totals);
                if (signup.Status == SignupStatus.Confirmed)
                {
                    totals = (totals.Confirmed + 1, totals.Pending, totals.Hours + hours);
                }
                else
                {
                    totals = (totals.Confirmed, totals.Pending + 1, totals.Hours);
                }
                perUser[signup.UserId] = totals;
            }
        }

        if (perUser.Count == 0) return new List<WorkloadByPersonRow>();

        var users = await _userService.GetUserInfosAsync(perUser.Keys.ToList(), ct);

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
                    ConfirmedHours: kvp.Value.Hours);
            })
            .ToList();
    }
}
