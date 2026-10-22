using NodaTime;

namespace Humans.Finance.Models;

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
    /// Cumulative expenses (ignoring income) have exceeded gross ticket revenue: the first period where the
    /// expense-only runway runs out.
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
