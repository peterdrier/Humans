using Humans.Base.Enums;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Shifts.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Domain;
using Humans.Shifts.Models;
using Humans.Shifts.Services.Dtos;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Shifts.Services;

internal sealed class ShiftDashboardMetrics(
    IShiftManagementRepository repo,
    EventCalendarResolver calendarResolver,
    IServiceProvider serviceProvider,
    IClock clock) : IApplicationService
{
    private ITeamService TeamService => serviceProvider.GetRequiredService<ITeamService>();
    private ITicketService TicketQueryService => serviceProvider.GetRequiredService<ITicketService>();
    private IUserService UserService => serviceProvider.GetRequiredService<IUserService>();

    internal async Task<DashboardOverview> ComputeDashboardOverviewAsync(Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod, CancellationToken ct)
    {
        var es = await calendarResolver.GetAsync(eventSettingsId, ct);
        if (es is null)
            return new DashboardOverview(0, 0, 0, 0, new PeriodBreakdown(0, 0, 0), 0, 0, 0, 0, []);

        var allShifts = await repo.GetEventShiftsAsync(new ShiftEventQuery(
            eventSettingsId,
            Flags: ShiftEventQueryFlags.ExcludeAdminOnly | ShiftEventQueryFlags.ExcludeHiddenRotas), ct);

        var shifts = period is null
            ? allShifts
            : allShifts.Where(s => s.GetShiftPeriod(es) == period.Value).ToList();

        if (period == ShiftPeriod.Build && subPeriod is not null)
        {
            var (start, end) = BuildSubPeriodClassifier.BoundsFor(subPeriod.Value, es);
            shifts = shifts.Where(s => s.DayOffset >= start && s.DayOffset < end).ToList();
        }

        var shiftIds = shifts.Select(s => s.Id).ToList();
        var confirmedCountsRo = await repo.GetConfirmedSignupCountsByShiftAsync(shiftIds, ct);
        var confirmedCounts = confirmedCountsRo.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        var aggregate = ShiftStaffingAggregation.AggregateShifts(shifts, confirmedCounts, es);
        var periodFillRates = new PeriodBreakdown(
            Pct(aggregate.Build), Pct(aggregate.Event), Pct(aggregate.Strike));

        var ticketOrders = await TicketQueryService.GetTicketOrdersAsync(ct);
        var ticketHolders = ticketOrders
            .Where(o => o.IsCurrentEvent && o.PaymentStatus == TicketPaymentStatus.Paid && o.MatchedUserId.HasValue)
            .Select(o => o.MatchedUserId!.Value)
            .ToHashSet();

        var engagedUserIds = await repo.GetEngagedUserIdsForShiftsAsync(shiftIds, ct);
        var engaged = engagedUserIds.ToHashSet();

        var ticketHoldersEngaged = engaged.Count(ticketHolders.Contains);
        var nonTicketSignups = engaged.Count(u => !ticketHolders.Contains(u));

        var staleThreshold = clock.GetCurrentInstant().Minus(Duration.FromDays(3));
        var stalePendingCount = await repo.GetStalePendingSignupCountAsync(shiftIds, staleThreshold, ct);

        var teamIdsOnRotas = shifts
            .Select(s => s.Rota.TeamId)
            .Distinct()
            .ToList();
        var teamsById = await TeamService.GetTeamsAsync(ct);
        var teamLookup = ShiftStaffingAggregation.IncludeParents(teamsById, teamIdsOnRotas);

        var departments = ShiftStaffingAggregation.BuildDepartmentRows(shifts, confirmedCounts, es, teamLookup);

        return new DashboardOverview(
            aggregate.Total, aggregate.Filled, aggregate.TotalSlots, aggregate.FilledSlots, periodFillRates,
            ticketHolders.Count, ticketHoldersEngaged, nonTicketSignups, stalePendingCount,
            departments);

        static double Pct(PeriodStaffing staffing) =>
            staffing.TotalSlots > 0 ? 100.0 * staffing.FilledSlots / staffing.TotalSlots : 0d;
    }

    internal async Task<IReadOnlyList<DashboardTrendPoint>> ComputeDashboardTrendsAsync(
        Guid eventSettingsId, TrendWindow window, ShiftPeriod? period, BuildSubPeriod? subPeriod, CancellationToken ct)
    {
        // CreatedAt is Shifts-owned, not on the Settings calendar; TrendWindow.All starts there.
        var local = await repo.GetEventSettingsByIdAsync(eventSettingsId, ct);
        if (local is null) return [];
        var es = await calendarResolver.GetAsync(eventSettingsId, ct);
        if (es is null) return [];

        var tz = DateTimeZoneProviders.Tzdb.GetZoneOrNull(es.TimeZoneId) ?? DateTimeZone.Utc;
        var now = clock.GetCurrentInstant();
        var today = now.InZone(tz).Date;

        LocalDate start = window switch
        {
            TrendWindow.Last7Days => today.PlusDays(-6),
            TrendWindow.Last30Days => today.PlusDays(-29),
            TrendWindow.Last90Days => today.PlusDays(-89),
            TrendWindow.All => local.CreatedAt.InZone(tz).Date,
            _ => today.PlusDays(-29),
        };

        if (start > today) start = today;

        var startInstant = start.AtStartOfDayInZone(tz).ToInstant();
        var endInstant = today.PlusDays(1).AtStartOfDayInZone(tz).ToInstant();

        var (minDayOffset, maxDayOffset) = BuildSubPeriodClassifier.GetDayOffsetBounds(period, subPeriod, es);

        var signupsInWindow = await repo.GetSignupCreatedAtsInWindowAsync(
            eventSettingsId, startInstant, endInstant, minDayOffset, maxDayOffset, ct);

        var ticketsInWindow = (await TicketQueryService.GetTicketOrdersAsync(ct))
            .Where(o => o.IsCurrentEvent && o.PaymentStatus == TicketPaymentStatus.Paid
                && o.PurchasedAt >= startInstant
                && o.PurchasedAt < endInstant)
            .Select(o => o.PurchasedAt)
            .ToList();
        var loginsInWindow = (await UserService.GetAllUserInfosAsync(ct).ConfigureAwait(false))
            .Where(u => u.LastLoginAt >= startInstant && u.LastLoginAt < endInstant)
            .Select(u => u.LastLoginAt!.Value)
            .ToList();

        LocalDate ToLocalDate(Instant i) => i.InZone(tz).Date;

        var signupsByDay = signupsInWindow.GroupBy(ToLocalDate).ToDictionary(g => g.Key, g => g.Count());
        var ticketsByDay = ticketsInWindow.GroupBy(ToLocalDate).ToDictionary(g => g.Key, g => g.Count());
        var loginCountsByDay = loginsInWindow
            .GroupBy(ToLocalDate)
            .ToDictionary(g => g.Key, g => g.Count());

        var points = new List<DashboardTrendPoint>();
        for (var d = start; d <= today; d = d.PlusDays(1))
        {
            points.Add(new DashboardTrendPoint(
                d,
                signupsByDay.TryGetValue(d, out var s) ? s : 0,
                ticketsByDay.TryGetValue(d, out var t) ? t : 0,
                loginCountsByDay.TryGetValue(d, out var l) ? l : 0));
        }
        return points;
    }

    public async Task<PostEventStats?> GetPostEventStatsAsync(
        Guid eventSettingsId,
        CancellationToken ct = default)
    {
        var es = await calendarResolver.GetAsync(eventSettingsId, ct);
        if (es is null)
            return null;

        // All shifts, so totals include those with zero signups.
        var allShifts = await repo.GetEventShiftsAsync(new ShiftEventQuery(
            eventSettingsId,
            Flags: ShiftEventQueryFlags.ExcludeAdminOnly | ShiftEventQueryFlags.ExcludeHiddenRotas), ct);

        var signupCounts = await repo.GetSignupStatusCountsForEventAsync(eventSettingsId, ct);
        var countsByShiftId = signupCounts.ToDictionary(r => r.ShiftId);

        var teamIdsOnRotas = allShifts.Select(s => s.Rota.TeamId).Distinct().ToList();
        var teamLookup = ShiftStaffingAggregation.IncludeParents(await TeamService.GetTeamsAsync(ct), teamIdsOnRotas);

        string DeptNameOf(Guid deptId) =>
            teamLookup.TryGetValue(deptId, out var t) ? t.Name : string.Empty;

        string? DeptSlugOf(Guid deptId) =>
            teamLookup.TryGetValue(deptId, out var t) ? t.Slug : null;

        int totalConfirmed = 0;
        int totalNoShow = 0;
        int shiftsWithData = 0;

        var deptBuckets = new Dictionary<Guid, Dictionary<ShiftPeriod, (int Confirmed, int NoShow)>>();

        foreach (var shift in allShifts)
        {
            var deptId = ShiftStaffingAggregation.DepartmentIdOf(shift.Rota.TeamId, teamLookup);
            var period = shift.GetShiftPeriod(es);

            if (!deptBuckets.TryGetValue(deptId, out var periodBucket))
            {
                periodBucket = new Dictionary<ShiftPeriod, (int, int)>
                {
                    [ShiftPeriod.Build] = (0, 0),
                    [ShiftPeriod.Event] = (0, 0),
                    [ShiftPeriod.Strike] = (0, 0),
                };
                deptBuckets[deptId] = periodBucket;
            }

            if (!countsByShiftId.TryGetValue(shift.Id, out var counts))
                continue;

            totalConfirmed += counts.ConfirmedCount;
            totalNoShow += counts.NoShowCount;
            if (counts.ConfirmedCount + counts.NoShowCount > 0)
                shiftsWithData++;

            var cur = periodBucket[period];
            periodBucket[period] = (cur.Confirmed + counts.ConfirmedCount, cur.NoShow + counts.NoShowCount);
        }

        PostEventPeriodRow ToPeriodRow(Dictionary<ShiftPeriod, (int Confirmed, int NoShow)> bucket, ShiftPeriod p)
        {
            var v = bucket[p];
            return new PostEventPeriodRow(v.Confirmed, v.NoShow);
        }

        var departments = deptBuckets
            .Select(kvp => new PostEventDepartmentRow(
                DepartmentId: kvp.Key,
                DepartmentName: DeptNameOf(kvp.Key),
                DepartmentSlug: DeptSlugOf(kvp.Key),
                TotalConfirmed: kvp.Value.Values.Sum(v => v.Confirmed),
                TotalNoShow: kvp.Value.Values.Sum(v => v.NoShow),
                Build: ToPeriodRow(kvp.Value, ShiftPeriod.Build),
                Event: ToPeriodRow(kvp.Value, ShiftPeriod.Event),
                Strike: ToPeriodRow(kvp.Value, ShiftPeriod.Strike)))
            .ToList();

        return new PostEventStats(
            TotalShifts: allShifts.Count,
            ShiftsWithData: shiftsWithData,
            TotalConfirmed: totalConfirmed,
            TotalNoShow: totalNoShow,
            Departments: departments);
    }
}
