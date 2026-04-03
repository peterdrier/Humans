using NodaTime;

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

/// <summary>
/// A synthetic VAT cash flow entry computed from a line item with VatRate > 0.
/// Amount is signed: negative = expense (VAT liability from income), positive = income (VAT credit from expenses).
/// SettlementDate is ~45 days after the quarter end containing the source item's ExpectedDate.
/// </summary>
public sealed class VatCashFlowEntry
{
    public required string CategoryName { get; init; }
    public decimal Amount { get; init; }
    public required LocalDate SettlementDate { get; init; }
}
