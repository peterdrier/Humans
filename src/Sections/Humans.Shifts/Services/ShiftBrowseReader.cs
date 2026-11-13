using Humans.Auth.Contracts;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Domain;
using Humans.Shifts.Helpers;
using Humans.Shifts.Models;
using Humans.Shifts.Services.Dtos;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Shifts.Services;

internal sealed class ShiftBrowseReader(
    EventCalendarResolver calendarResolver,
    IServiceProvider serviceProvider,
    IClock clock) : IApplicationService
{
    private ITeamService TeamService => serviceProvider.GetRequiredService<ITeamService>();
    private IRoleAssignmentService RoleAssignmentService => serviceProvider.GetRequiredService<IRoleAssignmentService>();
    private IUserService UserService => serviceProvider.GetRequiredService<IUserService>();
    private IShiftRotaRows RotaRows => serviceProvider.GetRequiredService<IShiftRotaRows>();

    internal async Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamLookupWithParentsAsync(
        IEnumerable<Guid> teamIds,
        CancellationToken ct = default)
    {
        var teamsById = await TeamService.GetTeamsAsync(ct);
        return ShiftStaffingAggregation.IncludeParents(teamsById, teamIds);
    }

    public async Task<bool> IsDeptCoordinatorAsync(Guid userId, Guid departmentTeamId, CancellationToken ct = default)
    {
        var teamIds = await GetCoordinatorTeamIdsAsync(userId, ct);
        if (teamIds.Contains(departmentTeamId))
            return true;

        var team = await TeamService.GetTeamAsync(departmentTeamId, ct);
        return team?.ParentTeamId is not null && teamIds.Contains(team.ParentTeamId.Value);
    }

    public async Task<bool> CanApproveSignupsAsync(Guid userId, Guid departmentTeamId, CancellationToken ct = default)
    {
        if (await RoleAssignmentService.HasActiveRoleAsync(userId, RoleNames.Admin, ct) ||
            await RoleAssignmentService.HasActiveRoleAsync(userId, ShiftsRoles.ShiftsNoInfo, ct) ||
            await RoleAssignmentService.HasActiveRoleAsync(userId, ShiftsRoles.ShiftsAdmin, ct))
            return true;

        return await IsDeptCoordinatorAsync(userId, departmentTeamId, ct);
    }

    // No cache of its own: Teams' singleton decorator answers from its in-RAM model, current on every team write.
    public Task<IReadOnlyList<Guid>> GetCoordinatorTeamIdsAsync(Guid userId, CancellationToken ct = default) =>
        TeamService.GetUserCoordinatedTeamIdsAsync(userId, ct);

    public async Task<IReadOnlyList<UrgentShiftInfo>> GetUrgentShiftsAsync(
        Guid eventSettingsId, int? limit = null,
        Guid? departmentId = null,
        LocalDate? startDate = null, LocalDate? endDate = null,
        ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null,
        bool includeRestricted = false, CancellationToken ct = default)
    {
        var es = await calendarResolver.GetAsync(eventSettingsId, ct);
        if (es is null) return [];

        var (minDayOffset, maxDayOffset) = BuildSubPeriodClassifier.GetDayOffsetBounds(period, subPeriod, es);

        // A reversed range is swapped. A single bound stays open-ended; do not mirror it onto the missing side.
        if (startDate.HasValue || endDate.HasValue)
        {
            var s = startDate;
            var e = endDate;
            if (s.HasValue && e.HasValue && s.Value > e.Value)
            {
                (s, e) = (e, s);
            }
            minDayOffset = s.HasValue
                ? Period.Between(es.GateOpeningDate, s.Value, PeriodUnits.Days).Days
                : null;
            maxDayOffset = e.HasValue
                ? Period.Between(es.GateOpeningDate, e.Value, PeriodUnits.Days).Days
                : null;
        }

        var urgencyTeamIds = await ResolveDepartmentTeamIdsAsync(departmentId, ct);
        var shifts = await GetEventShiftsAsync(
            eventSettingsId, urgencyTeamIds, minDayOffset, maxDayOffset,
            includeAdminOnly: includeRestricted, includeHidden: includeRestricted, ct);

        var teamIds = shifts.Select(s => s.Rota.TeamId).Distinct().ToList();
        var teamLookup = await GetTeamLookupWithParentsAsync(teamIds, ct);

        var now = clock.GetCurrentInstant();
        var urgentShifts = shifts
            .Where(s => s.Shift.Shift.GetAbsoluteEnd(es) > now)
            .Select(s =>
            {
                var confirmedCount = s.Shift.ConfirmedCount;
                var score = ShiftUrgencyRanking.CalculateScore(s.Shift.Shift, s.Rota.Priority, confirmedCount, es, clock);
                var remaining = Math.Max(0, s.Shift.Shift.MaxVolunteers - confirmedCount);
                var teamName = teamLookup.TryGetValue(s.Rota.TeamId, out var t) ? t.Name : string.Empty;
                return ToUrgentShiftInfo(s.Shift.Shift, s.Rota, es, score, confirmedCount, remaining, teamName, []);
            })
            .Where(u => u.UrgencyScore > 0)
            .OrderByDescending(u => u.UrgencyScore)
            .ToList();

        if (limit.HasValue)
            return ShiftUrgencyRanking.ApplyPeriodDiverseLimit(urgentShifts, limit.Value);

        return urgentShifts;
    }

    public async Task<IReadOnlyList<UrgentShiftInfo>> GetBrowseShiftsAsync(ShiftBrowseQuery query, CancellationToken ct = default)
    {
        var es = await calendarResolver.GetAsync(query.EventSettingsId, ct);
        if (es is null) return [];

        int? fromOffset = query.FromDate.HasValue
            ? Period.Between(es.GateOpeningDate, query.FromDate.Value, PeriodUnits.Days).Days
            : null;
        int? toOffset = query.ToDate.HasValue
            ? Period.Between(es.GateOpeningDate, query.ToDate.Value, PeriodUnits.Days).Days
            : null;

        var includeAdminOnly = query.Flags.HasFlag(ShiftBrowseQueryFlags.IncludeAdminOnly);
        var includeSignups = query.Flags.HasFlag(ShiftBrowseQueryFlags.IncludeSignups);
        var includeHidden = query.Flags.HasFlag(ShiftBrowseQueryFlags.IncludeHidden);

        var departmentTeamIds = await ResolveDepartmentTeamIdsAsync(query.DepartmentId, ct);

        var shifts = await GetEventShiftsAsync(
            query.EventSettingsId, departmentTeamIds, fromOffset, toOffset, includeAdminOnly, includeHidden, ct);

        var teamIds = shifts.Select(s => s.Rota.TeamId).Distinct().ToList();
        var teamLookup = await GetTeamLookupWithParentsAsync(teamIds, ct);

        var lockedTeams = new HashSet<Guid>();
        if (es.IsEarlyEntryClosed(clock.GetCurrentInstant()))
            foreach (var teamId in teamIds)
                if (query.ViewerUserId is not { } viewerId || !await CanApproveSignupsAsync(viewerId, teamId, ct))
                    lockedTeams.Add(teamId);

        IReadOnlyDictionary<Guid, UserInfo>? userLookup = null;
        if (includeSignups)
        {
            var userIds = shifts
                .SelectMany(s => s.Shift.Signups)
                .Where(ss => ss.Status is SignupStatus.Confirmed or SignupStatus.Pending)
                .Select(ss => ss.UserId)
                .Distinct()
                .ToList();
            if (userIds.Count > 0)
                userLookup = await UserService.GetUserInfosAsync(userIds, ct);
        }

        return shifts
            .Select(s =>
            {
                var confirmedCount = s.Shift.ConfirmedCount;
                var score = ShiftUrgencyRanking.CalculateScore(s.Shift.Shift, s.Rota.Priority, confirmedCount, es, clock);
                var remaining = Math.Max(0, s.Shift.Shift.MaxVolunteers - confirmedCount);
                var teamName = teamLookup.TryGetValue(s.Rota.TeamId, out var t) ? t.Name : string.Empty;
                var signups = includeSignups
                    ? s.Shift.Signups
                        .Where(ss => ss.Status is SignupStatus.Confirmed or SignupStatus.Pending)
                        .Select(ss =>
                        {
                            UserInfo? user = null;
                            userLookup?.TryGetValue(ss.UserId, out user);
                            return new ShiftSignupInfo(ss.UserId, user?.BurnerName ?? string.Empty, ss.Status);
                        })
                        .ToList()
                    : [];
                return ToUrgentShiftInfo(s.Shift.Shift, s.Rota, es, score, confirmedCount, remaining, teamName, signups)
                    with
                { EarlyEntrySignupsClosed = lockedTeams.Contains(s.Rota.TeamId) };
            })
            .ToList();
    }

    /// <summary>One event's shifts from the cached rotas, filtered the way the browse pages need.</summary>
    private async Task<IReadOnlyList<(ShiftAdminShift Shift, RotaInfo Rota)>> GetEventShiftsAsync(
        Guid eventSettingsId, IReadOnlyCollection<Guid>? teamIds, int? minDayOffset, int? maxDayOffset,
        bool includeAdminOnly, bool includeHidden, CancellationToken ct)
    {
        var rotas = await RotaRows.GetRotaRowsAsync(ct: ct);
        return rotas
            .Where(r => r.Rota.EventSettingsId == eventSettingsId
                && (includeHidden || r.Rota.IsVisibleToVolunteers)
                && (teamIds is not { Count: > 0 } || teamIds.Contains(r.Rota.TeamId)))
            .SelectMany(r => r.Shifts
                .Where(s => (includeAdminOnly || !s.Shift.AdminOnly)
                    && (minDayOffset is not { } min || s.Shift.DayOffset >= min)
                    && (maxDayOffset is not { } max || s.Shift.DayOffset <= max))
                .Select(s => (s, r.Rota)))
            .ToList();
    }

    // Mirrors the pie bucketing in GetDepartmentCoveragePiesAsync: non-promoted sub-teams roll into the parent,
    // promoted ones have their own pie and filter.
    private async Task<IReadOnlyCollection<Guid>?> ResolveDepartmentTeamIdsAsync(Guid? departmentId, CancellationToken ct = default)
    {
        if (!departmentId.HasValue) return null;
        var allTeams = await TeamService.GetTeamsAsync(ct);
        return ShiftStaffingAggregation.ResolveDepartmentTeamIds(departmentId.Value, allTeams);
    }

    /// <summary>
    /// Resolves the event-relative scalars here, where the event is in scope, so no consumer fetches the burn to
    /// render a row.
    /// </summary>
    private static UrgentShiftInfo ToUrgentShiftInfo(
        ShiftInfo shift,
        RotaInfo rota,
        BurnSettingsInfo es,
        double score,
        int confirmedCount,
        int remainingSlots,
        string departmentName,
        IReadOnlyList<ShiftSignupInfo> signups) =>
        new(
            shift,
            rota,
            departmentName,
            es.GateOpeningDate.PlusDays(shift.DayOffset),
            shift.GetShiftPeriod(es),
            shift.GetAbsoluteStart(es),
            shift.GetAbsoluteEnd(es),
            score,
            confirmedCount,
            remainingSlots,
            signups);

    internal static ShiftInfo ToShiftInfo(Shift shift) =>
        new(
            shift.Id,
            shift.RotaId,
            shift.Description,
            shift.DayOffset,
            shift.StartTime,
            shift.Duration.TotalHours,
            shift.MinVolunteers,
            shift.MaxVolunteers,
            shift.AdminOnly,
            shift.IsAllDay,
            shift.IsEarlyEntry);

    internal static RotaInfo ToRotaInfo(Rota rota) =>
        new(
            rota.Id,
            rota.EventSettingsId,
            rota.TeamId,
            rota.Name,
            rota.Description,
            rota.PracticalInfo,
            rota.Priority,
            rota.Policy,
            rota.Period,
            rota.IsVisibleToVolunteers,
            rota.Tags.Select(t => new ShiftTagSummary(t.Id, t.Name)).ToList());
}
