namespace Humans.Application.DTOs.Finance;

/// <summary>
/// One row of the budget-derived Holded tag inventory: a Year + Group + Category
/// triple flattened with the synthesized tag string (<c>group-slug-category-slug</c>).
/// Returned by <see cref="Humans.Application.Interfaces.Budget.IBudgetService.GetTagInventoryAsync"/>
/// so the Finance section can reconcile expected tags against Holded's tag list
/// without having to walk the budget graph itself.
/// </summary>
public sealed record HoldedTagInventoryRow(
    Guid BudgetYearId,
    string Year,
    Guid BudgetGroupId,
    string GroupName,
    string GroupSlug,
    Guid BudgetCategoryId,
    string CategoryName,
    string CategorySlug,
    string Tag);
