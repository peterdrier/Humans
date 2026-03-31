using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Web.Models;

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
    public required IReadOnlyList<TeamOption> Teams { get; init; }
}

public class TeamOption
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
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
