using Humans.Teams.Contracts;
using Humans.Budget.Contracts;
using Humans.Budget.Services;
using NodaTime;

namespace Humans.Budget.Models;

internal sealed class FinanceOverviewViewModel
{
    public required BudgetYearDetail Year { get; init; }
    public required IReadOnlyList<BudgetYearSummarySnapshot> AllYears { get; init; }

    // Summary data (same logic as public summary, but includes restricted groups)
    public decimal TotalIncome { get; init; }
    public decimal TotalExpenses { get; init; }
    public decimal NetBalance { get; init; }
    public required IReadOnlyList<BudgetSlice> IncomeSlices { get; init; }
    public required IReadOnlyList<BudgetSlice> ExpenseSlices { get; init; }

    /// <summary>Holded actual spend per budget category (keyed by BudgetCategoryId).</summary>
    public IReadOnlyDictionary<Guid, decimal> HoldedActualsByCategory { get; init; } =
        new Dictionary<Guid, decimal>();
}

internal sealed class TicketingProjectionUpdateForm
{
    public DateTime? StartDate { get; init; }
    public DateTime? EventDate { get; init; }
    public int InitialSalesCount { get; init; }
    public decimal DailySalesRate { get; init; }
    public decimal AverageTicketPrice { get; init; }
    public int VatRate { get; init; }
    public decimal StripeFeePercent { get; init; }
    public decimal StripeFeeFixed { get; init; }
    public decimal TicketTailorFeePercent { get; init; }
    public Guid BudgetYearId { get; init; }
}

internal sealed class CoordinatorBudgetViewModel
{
    public required BudgetYearDetail Year { get; init; }
    public required HashSet<Guid> EditableTeamIds { get; init; }
    public bool IsFinanceAdmin { get; init; }
}

internal sealed class CoordinatorCategoryDetailViewModel
{
    public required BudgetCategorySnapshot Category { get; init; }
    public bool CanEdit { get; init; }
    public bool IsFinanceAdmin { get; init; }
    public required IReadOnlyList<TeamInfo> Teams { get; init; }
}

internal sealed class BudgetSummaryViewModel
{
    public required string YearName { get; init; }
    public decimal TotalIncome { get; init; }
    public decimal TotalExpenses { get; init; }
    public decimal NetBalance { get; init; }
    public decimal TotalLineItems { get; init; }
    public required IReadOnlyList<BudgetSlice> IncomeSlices { get; init; }
    public required IReadOnlyList<BudgetSlice> ExpenseSlices { get; init; }
    public bool IsCoordinator { get; init; }
}

internal sealed class BudgetSlice
{
    public required string Name { get; init; }
    public decimal Amount { get; init; }
    public decimal Percentage { get; init; }
}

/// <summary>
/// Represents a virtual VAT cash flow entry computed from a line item with VatRate > 0.
/// </summary>
internal sealed class VatProjection
{
    public required string SourceDescription { get; init; }
    public decimal VatAmount { get; init; }
    public LocalDate SettlementDate { get; init; }
    public int VatRate { get; init; }
    public bool IsExpense { get; init; }

    /// <summary>
    /// View-model shaping only — the tax math (VAT portion, settlement date) is
    /// <c>BudgetService</c>'s, called statically. Ordered by settlement date.
    /// </summary>
    public static List<VatProjection> ForLineItems(IEnumerable<Humans.Budget.Contracts.BudgetCategoryLineItemSnapshot> lineItems) =>
        [.. lineItems
            .Where(li => li.VatRate > 0 && li.ExpectedDate.HasValue)
            .Select(li => new VatProjection
            {
                SourceDescription = li.Description,
                VatAmount = Services.BudgetService.ComputeVatPortion(li.Amount, li.VatRate),
                SettlementDate = Services.BudgetService.ComputeVatSettlementDate(li.ExpectedDate!.Value),
                VatRate = li.VatRate,
                IsExpense = li.Amount > 0
            })
            .OrderBy(v => v.SettlementDate)];
}

internal sealed class CashFlowViewModel
{
    public required string YearName { get; init; }
    public required string Period { get; init; } // "weekly" or "monthly"
    public required IReadOnlyList<CashFlowPeriodRow> Periods { get; init; }
    public required CashFlowUnscheduledSummary Unscheduled { get; init; }
}

internal sealed class CashFlowPeriodRow
{
    public required string Label { get; init; }
    public required LocalDate PeriodStart { get; init; }
    public required LocalDate PeriodEnd { get; init; }
    public decimal IncomeTotal { get; init; }
    public decimal ExpenseTotal { get; init; }
    public decimal Net { get; init; }
    public decimal RunningNet { get; init; }

    /// <summary>
    /// True when cumulative expenses (ignoring income) have exceeded gross ticket revenue.
    /// Used for the "Funds Exhausted" pill — marks the first period where the expense-only
    /// runway runs out.
    /// </summary>
    public bool FundsExhausted { get; init; }

    public required IReadOnlyList<CashFlowCategoryRow> Categories { get; init; }
}

internal sealed class CashFlowCategoryRow
{
    public required string CategoryName { get; init; }
    public required string GroupName { get; init; }
    public decimal Amount { get; init; }
}

internal sealed class CashFlowUnscheduledSummary
{
    public decimal IncomeTotal { get; init; }
    public decimal ExpenseTotal { get; init; }
    public decimal Net { get; init; }
    public required IReadOnlyList<CashFlowCategoryRow> Categories { get; init; }
}
