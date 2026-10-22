using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Budget.Contracts;
using Humans.Finance.Models;
using Humans.Tickets.Contracts;
using NodaTime;

namespace Humans.Finance.Services;

/// <summary>
/// <c>/Finance/CashFlow</c>: the active budget year's line items plus synthetic VAT settlements,
/// bucketed by week or month, with running net and an expense-only runway seeded from gross
/// ticket revenue. Reads Budget and Tickets through their public contracts; owns no tables.
/// </summary>
internal sealed class CashFlowService(IBudgetService budgetService, ITicketService ticketService) : IApplicationService
{
    /// <summary>The active year's cash flow, or null when no year is active.</summary>
    public async Task<CashFlowViewModel?> GetActiveYearCashFlowAsync(string period, CancellationToken ct = default)
    {
        var year = await budgetService.GetActiveYearAsync(ct);
        if (year is null) return null;

        var grossTicketRevenue = await GetGrossTicketRevenueAsync(year, ct);
        return Build(year, period, grossTicketRevenue);
    }

    /// <summary>
    /// Gross revenue of the year's paid ticket orders: a year with a configured
    /// <c>VendorEventId</c> counts only that event's orders, an unconfigured year the current event's.
    /// </summary>
    internal async Task<decimal> GetGrossTicketRevenueAsync(BudgetYearDetail year, CancellationToken ct = default) =>
        (await ticketService.GetTicketOrdersAsync(ct))
            .Where(o => o.PaymentStatus == TicketPaymentStatus.Paid && o.IsForEvent(year.VendorEventId))
            .Sum(o => o.TotalAmount);

    /// <summary>Aggregates by period; an unknown period falls back to monthly.</summary>
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

    /// <summary>
    /// One synthetic settlement per dated line item with VAT: income owes its VAT portion
    /// ("VAT Liability", negative), an expense reclaims it ("VAT Credits", positive).
    /// </summary>
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

    /// <summary>The VAT portion of a VAT-inclusive amount: |amount| × rate / (100 + rate).</summary>
    private static decimal VatPortion(decimal amount, int vatRate) =>
        Math.Abs(amount) * vatRate / (100m + vatRate);

    /// <summary>Spanish quarterly VAT: settlement lands 45 days after the quarter-end of the expected date.</summary>
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

    /// <summary>Cash flow entry — real line item or synthetic VAT settlement.</summary>
    internal sealed record CashFlowEntry(string GroupName, string CategoryName, decimal Amount, LocalDate? Date);

    private sealed record CashFlowPeriodGroup(
        string Label,
        LocalDate PeriodStart,
        LocalDate PeriodEnd,
        List<CashFlowEntry> Items);
}
