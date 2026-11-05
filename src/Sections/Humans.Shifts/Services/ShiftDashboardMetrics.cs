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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Shifts.Services;

internal sealed class ShiftDashboardMetrics(
    IShiftManagementRepository repo,
    EventCalendarResolver calendarResolver,
    IServiceProvider serviceProvider,
    IMemoryCache cache,
    IClock clock,
    TimeSpan cacheTtl) : IApplicationService
{
    private ITeamService TeamService => serviceProvider.GetRequiredService<ITeamService>();
    private ITicketService TicketQueryService => serviceProvider.GetRequiredService<ITicketService>();
    private IUserService UserService => serviceProvider.GetRequiredService<IUserService>();

    internal static string OverviewCacheKey(Guid eventId, ShiftPeriod? period) =>
        $"dashboard-overview:{eventId}:{period?.ToString() ?? "all"}";
    internal static string TrendsCacheKey(Guid eventId, TrendWindow window, ShiftPeriod? period) =>
        $"dashboard-trends:{eventId}:{window}:{period?.ToString() ?? "all"}";

    public async Task<DashboardOverview> GetDashboardOverviewAsync(Guid eventSettingsId, ShiftPeriod? period = null, BuildSubPeriod? subPeriod = null, CancellationToken ct = default)
    {
        // Sub-periods would fan the cache key out 4x, so only the base period is cached.
        if (subPeriod is not null)
            return await ComputeDashboardOverviewAsync(eventSettingsId, period, subPeriod, ct);

        var cached = await cache.GetOrCreateAsync(OverviewCacheKey(eventSettingsId, period), async entry =>
        {
            entry.SlidingExpiration = cacheTtl;
            return await ComputeDashboardOverviewAsync(eventSettingsId, period, subPeriod: null, ct);
        });
        return cached!;
    }

    private async Task<DashboardOverview> ComputeDashboardOverviewAsync(Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod, CancellationToken ct)
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

    public async Task<IReadOnlyList<DashboardTrendPoint>> GetDashboardTrendsAsync(
        Guid eventSettingsId, TrendWindow window, ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null, CancellationToken ct = default)
    {
        // Sub-periods would fan the cache key out 4x, so only the base period is cached.
        if (subPeriod is not null)
            return await ComputeDashboardTrendsAsync(eventSettingsId, window, period, subPeriod, ct);

        var cached = await cache.GetOrCreateAsync(TrendsCacheKey(eventSettingsId, window, period), async entry =>
        {
            entry.SlidingExpiration = cacheTtl;
            return await ComputeDashboardTrendsAsync(eventSettingsId, window, period, subPeriod: null, ct);
        });
        return cached!;
    }

    private async Task<IReadOnlyList<DashboardTrendPoint>> ComputeDashboardTrendsAsync(
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
}
