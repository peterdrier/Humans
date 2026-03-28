using Humans.Domain.Entities;

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
    public decimal TotalBudget { get; init; }
    public decimal TotalLineItems { get; init; }
    public required IReadOnlyList<BudgetSlice> Slices { get; init; }
    public bool IsCoordinator { get; init; }
}

public class BudgetSlice
{
    public required string Name { get; init; }
    public decimal Amount { get; init; }
    public decimal Percentage { get; init; }
}
