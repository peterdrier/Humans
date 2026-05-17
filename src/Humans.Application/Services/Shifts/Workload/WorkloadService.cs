using Humans.Application.DTOs.Shifts.Workload;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Shifts.Workload;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;

namespace Humans.Application.Services.Shifts.Workload;

/// <summary>
/// Shifts-domain workload aggregations for the coordinator workload dashboard.
/// </summary>
/// <remarks>
/// <para>
/// Reads through <see cref="IShiftManagementRepository"/> (Shifts-owned) and
/// stitches display names via <see cref="ITeamService"/> /
/// <see cref="IUserService"/> per the no-cross-section-EF-joins rule.
/// </para>
/// <para>
/// §15 Option B: service-level <see cref="IMemoryCache"/> with a 5-minute sliding
/// expiration (same TTL as the existing shift dashboard analytics). Workload
/// queries scan every shift + signup for the active event — fine at our ~500-user
/// scale but worth caching across multiple sort/filter requests on the page.
/// Invalidation is intentionally TTL-only; mutations don't ping the cache,
/// matching the dashboard pattern.
/// </para>
/// </remarks>
public sealed class WorkloadService : IWorkloadService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly decimal AllDayShiftHours = (decimal)Duration.FromTicks(
        Shift.AllDayWindowEnd.TickOfDay - Shift.AllDayWindowStart.TickOfDay).TotalHours;

    private readonly IShiftManagementRepository _repo;
    private readonly ITeamService _teamService;
    private readonly IUserService _userService;
    private readonly IMemoryCache _cache;

    public WorkloadService(
        IShiftManagementRepository repo,
        ITeamService teamService,
        IUserService userService,
        IMemoryCache cache)
    {
        _repo = repo;
        _teamService = teamService;
        _userService = userService;
        _cache = cache;
    }

    private static string CacheKey(Guid eventId) => $"workload-report:{eventId}";

    public async Task<WorkloadReport?> GetForActiveEventAsync(CancellationToken ct = default)
    {
        var es = await _repo.GetActiveEventSettingsAsync(ct);
        if (es is null) return null;

        // IMemoryCache.GetOrCreateAsync swallows CT; we accept that — the inner
        // compute is short and the TTL is fine if a cancellation races a build.
        return await _cache.GetOrCreateAsync(CacheKey(es.Id), async entry =>
        {
            entry.SlidingExpiration = CacheTtl;
            return await ComputeAsync(es, ct);
        });
    }

    private async Task<WorkloadReport> ComputeAsync(EventSettings es, CancellationToken ct)
    {
        // Pull every shift + signup for the event. AdminOnly + hidden rotas are
        // INCLUDED — the workload view is admin-only and coordinators need full
        // visibility for balancing.
        var shifts = await _repo.GetShiftsWithSignupsForEventAsync(
            eventSettingsId: es.Id,
            departmentId: null,
            includeAdminOnly: true,
            includeHidden: true,
            fromDayOffset: null,
            toDayOffset: null,
            includeRotaTags: false,
            ct);

        var teamIds = shifts.Select(s => s.Rota.TeamId).Distinct().ToList();
        var teamLookup = teamIds.Count > 0
            ? await _teamService.GetByIdsWithParentsAsync(teamIds, ct)
            : new Dictionary<Guid, Team>();

        var byShift = BuildByShift(shifts, es, teamLookup);
        var byDepartment = BuildByDepartment(shifts, teamLookup);
        var byPerson = await BuildByPersonAsync(shifts, ct);

        return new WorkloadReport(
            EventSettingsId: es.Id,
            EventYear: es.Year,
            ByPerson: byPerson,
            ByShift: byShift,
            ByDepartment: byDepartment);
    }

    private static decimal HoursOf(Shift shift) =>
        shift.IsAllDay ? AllDayShiftHours : (decimal)shift.Duration.TotalHours;

    private static List<WorkloadByShiftRow> BuildByShift(
        IReadOnlyList<Shift> shifts,
        EventSettings es,
        IReadOnlyDictionary<Guid, Team> teamLookup) =>
        shifts
            .Select(s =>
            {
                var confirmed = s.ShiftSignups.Count(ss => ss.Status == SignupStatus.Confirmed);
                var pending = s.ShiftSignups.Count(ss => ss.Status == SignupStatus.Pending);
                var teamName = teamLookup.TryGetValue(s.Rota.TeamId, out var team) ? team.Name : "(unknown)";
                return new WorkloadByShiftRow(
                    ShiftId: s.Id,
                    RotaId: s.RotaId,
                    RotaName: s.Rota.Name,
                    TeamId: s.Rota.TeamId,
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
            .OrderBy(r => r.DayOffset)
            .ThenBy(r => r.StartTime)
            .ThenBy(r => r.TeamName, StringComparer.Ordinal)
            .ToList();

    private static List<WorkloadByDepartmentRow> BuildByDepartment(
        IReadOnlyList<Shift> shifts,
        IReadOnlyDictionary<Guid, Team> teamLookup) =>
        shifts
            .GroupBy(s => s.Rota.TeamId)
            .Select(g =>
            {
                var hoursPerShift = g.ToDictionary(s => s.Id, HoursOf);
                var plannedSlots = g.Sum(s => s.MaxVolunteers);
                var filledSlots = g.Sum(s => Math.Min(
                    s.ShiftSignups.Count(ss => ss.Status == SignupStatus.Confirmed),
                    s.MaxVolunteers));
                var plannedHours = g.Sum(s => hoursPerShift[s.Id] * s.MaxVolunteers);
                var filledHours = g.Sum(s => hoursPerShift[s.Id] *
                    Math.Min(s.ShiftSignups.Count(ss => ss.Status == SignupStatus.Confirmed), s.MaxVolunteers));
                var teamName = teamLookup.TryGetValue(g.Key, out var team) ? team.Name : "(unknown)";
                var rotaCount = g.Select(s => s.RotaId).Distinct().Count();
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
            .OrderBy(r => r.TeamName, StringComparer.Ordinal)
            .ToList();

    private async Task<List<WorkloadByPersonRow>> BuildByPersonAsync(
        IReadOnlyList<Shift> shifts,
        CancellationToken ct)
    {
        // Walk every signup once. Confirmed contributes hours; Pending bumps the
        // pending count only (don't inflate burnout signal from queued work).
        var perUser = new Dictionary<Guid, (int Confirmed, int Pending, decimal Hours)>();
        foreach (var shift in shifts)
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
            .OrderByDescending(r => r.ConfirmedHours)
            .ThenBy(r => r.DisplayName, StringComparer.Ordinal)
            .ToList();
    }
}
