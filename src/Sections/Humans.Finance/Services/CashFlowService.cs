using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Budget.Contracts;
using Humans.Finance.Models;
using Humans.Tickets.Contracts;
using NodaTime;

namespace Humans.Finance.Services;

/// <summary>The expense-only runway is seeded from gross ticket revenue.</summary>
internal sealed class CashFlowService(IBudgetService budgetService, ITicketService ticketService) : IApplicationService
{
    /// <summary>Null when no year is active.</summary>
    public async Task<CashFlowViewModel?> GetActiveYearCashFlowAsync(string period, CancellationToken ct = default)
    {
        var year = await budgetService.GetActiveYearAsync(ct);
        if (year is null) return null;

        var grossTicketRevenue = await GetGrossTicketRevenueAsync(year, ct);
        return Build(year, period, grossTicketRevenue);
    }

    /// <summary>
    /// A year with a configured <c>VendorEventId</c> counts only that event's orders, otherwise the current event's.
    /// Budget's TicketingBudgetService holds the same rule.
    /// </summary>
    internal async Task<decimal> GetGrossTicketRevenueAsync(BudgetYearDetail year, CancellationToken ct = default) =>
        (await ticketService.GetTicketOrdersAsync(ct))
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid && IsYearTicketOrder(year, o))
            .Sum(o => o.TotalAmount);

    /// <summary>An unknown period falls back to monthly.</summary>
    internal static CashFlowViewModel Build(BudgetYearDetail year, string period, decimal grossTicketRevenue)
    {
        if (!string.Equals(period, "weekly", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(period, "monthly", StringComparison.OrdinalIgnoreCase))
        {
            period = "monthly";
        }

        var allEntries = year.Groups
            .SelectMany(g => g.Categories.Select(c => new { GroupName = g.Name, CategoryName = c.Name, Category = c }))
            .SelectMany(ctx => ctx.Category.LineItems.Select(li => new CashFlowEntry(
                ctx.GroupName, ctx.CategoryName, li.Amount, li.ExpectedDate)))
            .ToList();

        allEntries.AddRange(ComputeVatEntries(year.Groups));

        var scheduled = allEntries.Where(x => x.Date.HasValue).ToList();
        var unscheduled = allEntries.Where(x => !x.Date.HasValue).ToList();

        var periodRows = new List<CashFlowPeriodRow>();

        if (scheduled.Count > 0)
        {
            var isWeekly = string.Equals(period, "weekly", StringComparison.OrdinalIgnoreCase);
            var grouped = isWeekly ? GroupByWeek(scheduled) : GroupByMonth(scheduled);

            decimal runningNet = 0;
            // Expense-only runway: gross ticket revenue minus expenses; income lines don't extend it.
            decimal runningExpenseBalance = grossTicketRevenue;
            var fundsExhausted = false;
            foreach (var pg in grouped.OrderBy(g => g.PeriodStart))
            {
                var periodIncome = pg.Items.Where(x => x.Amount > 0).Sum(x => x.Amount);
                var periodExpense = pg.Items.Where(x => x.Amount < 0).Sum(x => x.Amount);
                var periodNet = periodIncome + periodExpense;
                runningNet += periodNet;

                runningExpenseBalance += periodExpense;
                var isExhausted = !fundsExhausted && runningExpenseBalance <= 0;
                if (isExhausted)
                {
                    fundsExhausted = true;
                }

                var categories = BuildCategoryRows(pg.Items);

                periodRows.Add(new CashFlowPeriodRow
                {
                    Label = pg.Label,
                    PeriodStart = pg.PeriodStart,
                    PeriodEnd = pg.PeriodEnd,
                    IncomeTotal = periodIncome,
                    ExpenseTotal = periodExpense,
                    Net = periodNet,
                    RunningNet = runningNet,
                    FundsExhausted = isExhausted,
                    Categories = categories
                });
            }
        }

        var unscheduledIncome = unscheduled.Where(x => x.Amount > 0).Sum(x => x.Amount);
        var unscheduledExpense = unscheduled.Where(x => x.Amount < 0).Sum(x => x.Amount);
        var unscheduledCategories = BuildCategoryRows(unscheduled);

        return new CashFlowViewModel
        {
            YearName = year.Name,
            Period = period.ToLowerInvariant(),
            Periods = periodRows,
            Unscheduled = new CashFlowUnscheduledSummary
            {
                IncomeTotal = unscheduledIncome,
                ExpenseTotal = unscheduledExpense,
                Net = unscheduledIncome + unscheduledExpense,
                Categories = unscheduledCategories
            }
        };
    }

    /// <summary>Income owes its VAT portion ("VAT Liability", negative); an expense reclaims it ("VAT Credits").</summary>
    internal static IReadOnlyList<CashFlowEntry> ComputeVatEntries(IReadOnlyList<BudgetGroupDetail> groups) =>
        groups
            .SelectMany(g => g.Categories)
            .SelectMany(c => c.LineItems)
            .Where(li => li.VatRate > 0 && li.ExpectedDate.HasValue)
            .Select(li =>
            {
                var vatAmount = VatPortion(li.Amount, li.VatRate);
                return li.Amount > 0
                    ? new CashFlowEntry("VAT", "VAT Liability", -vatAmount, VatSettlementDate(li.ExpectedDate!.Value))
                    : new CashFlowEntry("VAT", "VAT Credits", vatAmount, VatSettlementDate(li.ExpectedDate!.Value));
            })
            .ToList();

    /// <summary>For a VAT-inclusive amount: |amount| × rate / (100 + rate).</summary>
    private static decimal VatPortion(decimal amount, int vatRate) =>
        Math.Abs(amount) * vatRate / (100m + vatRate);

    /// <summary>Spanish quarterly VAT: settlement lands 45 days after the expected date's quarter end.</summary>
    private static LocalDate VatSettlementDate(LocalDate expectedDate)
    {
        var quarterEnd = expectedDate.Month switch
        {
            >= 1 and <= 3 => new LocalDate(expectedDate.Year, 3, 31),
            >= 4 and <= 6 => new LocalDate(expectedDate.Year, 6, 30),
            >= 7 and <= 9 => new LocalDate(expectedDate.Year, 9, 30),
            _ => new LocalDate(expectedDate.Year, 12, 31)
        };
        return quarterEnd.PlusDays(45);
    }

    private static bool IsYearTicketOrder(BudgetYearDetail year, TicketOrderInfo order) =>
        year.VendorEventId is { } eventId
            ? string.Equals(order.VendorEventId, eventId, StringComparison.Ordinal)
            : order.IsCurrentEvent;

    private static List<CashFlowCategoryRow> BuildCategoryRows(List<CashFlowEntry> items)
    {
        return items
            .GroupBy(x => new { x.CategoryName, x.GroupName })
            .Select(cg => new CashFlowCategoryRow
            {
                CategoryName = cg.Key.CategoryName,
                GroupName = cg.Key.GroupName,
                Amount = cg.Sum(x => x.Amount)
            })
            .OrderBy(c => c.GroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.CategoryName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<CashFlowPeriodGroup> GroupByWeek(List<CashFlowEntry> items)
    {
        return items
            .GroupBy(x =>
            {
                var date = x.Date!.Value;
                var dayOfWeek = date.DayOfWeek;
                var monday = date.PlusDays(-(((int)dayOfWeek + 6) % 7));
                return monday;
            })
            .Select(g =>
            {
                var monday = g.Key;
                var sunday = monday.PlusDays(6);
                return new CashFlowPeriodGroup(
                    $"{monday.ToDateTimeUnspecified().ToWeekdayDayMonth()} - {sunday.ToDate()}",
                    monday,
                    sunday,
                    g.ToList());
            })
            .ToList();
    }

    private static List<CashFlowPeriodGroup> GroupByMonth(List<CashFlowEntry> items)
    {
        return items
            .GroupBy(x =>
            {
                var date = x.Date!.Value;
                return new { date.Year, date.Month };
            })
            .Select(g =>
            {
                var firstDay = new LocalDate(g.Key.Year, g.Key.Month, 1);
                var lastDay = firstDay.PlusDays(firstDay.Calendar.GetDaysInMonth(g.Key.Year, g.Key.Month) - 1);
                return new CashFlowPeriodGroup(
                    firstDay.ToDateTimeUnspecified().ToMonthYear(),
                    firstDay,
                    lastDay,
                    g.ToList());
            })
            .ToList();
    }

    internal sealed record CashFlowEntry(string GroupName, string CategoryName, decimal Amount, LocalDate? Date);

    private sealed record CashFlowPeriodGroup(
        string Label,
        LocalDate PeriodStart,
        LocalDate PeriodEnd,
        List<CashFlowEntry> Items);
}
