namespace Humans.Application.DTOs;

/// <summary>
/// Computed budget summary with income/expense totals and categorical breakdowns.
/// Produced by IBudgetService.ComputeBudgetSummary from in-memory group data.
/// </summary>
public sealed class BudgetSummaryResult
{
    public decimal TotalIncome { get; init; }
    public decimal TotalExpenses { get; init; }
    public decimal NetBalance { get; init; }
    public required IReadOnlyList<BudgetSliceResult> IncomeSlices { get; init; }
    public required IReadOnlyList<BudgetSliceResult> ExpenseSlices { get; init; }
}

public sealed class BudgetSliceResult
{
    public required string Name { get; init; }
    public decimal Amount { get; init; }
    public decimal Percentage { get; init; }
}
