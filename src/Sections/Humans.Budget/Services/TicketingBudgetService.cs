using Humans.Base.Extensions;
using Humans.Budget.Contracts;
using Humans.Tickets.Contracts;
using NodaTime;

namespace Humans.Budget.Services;

internal sealed class TicketingBudgetService(
    ITicketService ticketService,
    IBudgetManagement budgetService,
    IClock clock,
    ILogger<TicketingBudgetService> logger) : ITicketingBudgetService
{
    public async Task<int> SyncActualsAsync(
        Guid budgetYearId, Guid? actorUserId, CancellationToken ct = default)
    {
        var activeYear = await budgetService.GetActiveYearAsync();
        if (activeYear is null || activeYear.Id != budgetYearId)
            throw new InvalidOperationException(
                "Ticketing actuals can only be synced into the active budget year.");

        try
        {
            var orders = await ticketService.GetTicketOrdersAsync(ct);

            // No orders of any status for the year's event means a moved VendorEventId or missing
            // sync state, not "everything refunded": write nothing rather than zero the year.
            if (!orders.Any(o => IsYearTicketOrder(activeYear, o)))
            {
                logger.LogWarning(
                    "Ticketing sync for budget year {YearId} found no orders for its event; stored actuals left untouched",
                    budgetYearId);
                return 0;
            }

            var today = clock.GetCurrentInstant().InUtc().Date;
            var currentWeekMonday = GetIsoMonday(today);

            // Canonical English descriptions whatever the operator's UI language: BudgetRepository matches existing
            // actuals rows by description.
            List<TicketingWeeklyActuals> weeklyActuals;
            using (new CultureScope("en"))
            {
                weeklyActuals = orders
                    .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid && IsYearTicketOrder(activeYear, o))
                    .GroupBy(o => GetIsoMonday(o.PurchasedAt.InUtc().Date))
                    .Where(g => g.Key < currentWeekMonday)
                    .OrderBy(g => g.Key)
                    .Select(g =>
                    {
                        var monday = g.Key;
                        var sunday = monday.PlusDays(6);
                        return new TicketingWeeklyActuals(
                            Monday: monday,
                            Sunday: sunday,
                            WeekLabel: $"{monday.ToWeekdayDayMonth()}–{sunday.ToWeekdayDayMonth()}",
                            TicketCount: g.Sum(o => o.Attendees.Count(a =>
                                a.Status == TicketAttendeeStatus.Valid ||
                                a.Status == TicketAttendeeStatus.CheckedIn)),
                            Revenue: g.Sum(o => o.TotalAmount),
                            StripeFees: g.Sum(o => o.StripeFee ?? 0m),
                            TicketTailorFees: g.Sum(o => o.ApplicationFee ?? 0m),
                            StripeFeeTax: g.Sum(o => o.FeeTax ?? 0m),
                            // Null FeeTax = enriched before Stripe's tax bucket was captured: unknown, not zero.
                            StripeFeesTaxUnknown: g.Where(o => o.FeeTax is null).Sum(o => o.StripeFee ?? 0m),
                            StripeFeesUntaxed: g.Where(o => o.FeeTax == 0m).Sum(o => o.StripeFee ?? 0m));
                    })
                    .ToList();
            }

            return await budgetService.SyncTicketingActualsAsync(
                budgetYearId, weeklyActuals, actorUserId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to sync ticketing actuals for budget year {YearId}", budgetYearId);
            throw;
        }
    }

    public async Task<int> RefreshProjectionsAsync(
        Guid budgetYearId, Guid? actorUserId, CancellationToken ct = default)
    {
        try
        {
            return await budgetService.RefreshTicketingProjectionsAsync(budgetYearId, actorUserId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to refresh ticketing projections for budget year {YearId}", budgetYearId);
            throw;
        }
    }

    public async Task<int?> UpdateProjectionAndRefreshAsync(
        TicketingProjectionUpdateCommand command,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        var year = await budgetService.GetYearByIdAsync(command.BudgetYearId, ct);
        if (year?.Groups.Any(group => group.Id == command.BudgetGroupId) != true)
        {
            logger.LogWarning("Rejected ticketing projection for group {GroupId} outside budget year {YearId}",
                command.BudgetGroupId, command.BudgetYearId);
            return null;
        }

        await budgetService.UpdateTicketingProjectionAsync(
            command.BudgetGroupId,
            command.StartDate,
            command.EventDate,
            command.InitialSalesCount,
            command.DailySalesRate,
            command.AverageTicketPrice,
            command.VatRate,
            command.StripeFeePercent,
            command.StripeFeeFixed,
            command.TicketTailorFeePercent,
            actorUserId);

        return await RefreshProjectionsAsync(command.BudgetYearId, actorUserId, ct);
    }

    /// <summary>
    /// A year with a configured <c>VendorEventId</c> counts only that event's orders; otherwise the current event's.
    /// Finance's CashFlowService keeps its own copy of this rule.
    /// </summary>
    private static bool IsYearTicketOrder(BudgetYearDetail year, TicketOrderInfo order) =>
        year.VendorEventId is { } eventId
            ? string.Equals(order.VendorEventId, eventId, StringComparison.Ordinal)
            : order.IsCurrentEvent;

    private static LocalDate GetIsoMonday(LocalDate date)
    {
        var dayOfWeek = (int)date.DayOfWeek;
        return date.PlusDays(-(dayOfWeek - 1));
    }

}
