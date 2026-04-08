using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Web.Models;

public class FinanceOverviewViewModel
{
    public required BudgetYear Year { get; init; }
    public required IReadOnlyList<BudgetYear> AllYears { get; init; }

    // Summary data (same logic as public summary, but includes restricted groups)
    public decimal TotalIncome { get; init; }
    public decimal TotalExpenses { get; init; }
    public decimal NetBalance { get; init; }
    public required IReadOnlyList<BudgetSlice> IncomeSlices { get; init; }
    public required IReadOnlyList<BudgetSlice> ExpenseSlices { get; init; }
}

public class CoordinatorBudgetViewModel
{
    public required BudgetYear Year { get; init; }
    public required HashSet<Guid> EditableTeamIds { get; init; }
    public bool IsFinanceAdmin { get; init; }
}

public class CoordinatorCategoryDetailViewModel
{
    public required BudgetCategory Category { get; init; }
    public bool CanEdit { get; init; }
    public bool IsFinanceAdmin { get; init; }
    public required IReadOnlyList<TeamOptionDto> Teams { get; init; }
}

public class BudgetSummaryViewModel
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

public class BudgetSlice
{
    public required string Name { get; init; }
    public decimal Amount { get; init; }
    public decimal Percentage { get; init; }
}

/// <summary>
/// Represents a virtual VAT cash flow entry computed from a line item with VatRate > 0.
/// </summary>
public class VatProjection
{
    public required string SourceDescription { get; init; }
    public decimal VatAmount { get; init; }
    public LocalDate SettlementDate { get; init; }
    public int VatRate { get; init; }
    public bool IsExpense { get; init; }
}

// --- Cash Flow Projection View Models ---

public class CashFlowViewModel
{
    public required string YearName { get; init; }
    public required string Period { get; init; } // "weekly" or "monthly"
    public required IReadOnlyList<CashFlowPeriodRow> Periods { get; init; }
    public required CashFlowUnscheduledSummary Unscheduled { get; init; }
}

public class CashFlowPeriodRow
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

public class CashFlowCategoryRow
{
    public required string CategoryName { get; init; }
    public required string GroupName { get; init; }
    public decimal Amount { get; init; }
}

public class CashFlowUnscheduledSummary
{
    public decimal IncomeTotal { get; init; }
    public decimal ExpenseTotal { get; init; }
    public decimal Net { get; init; }
    public required IReadOnlyList<CashFlowCategoryRow> Categories { get; init; }
}
