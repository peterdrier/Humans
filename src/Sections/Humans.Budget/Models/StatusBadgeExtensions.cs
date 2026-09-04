using Humans.Budget.Contracts;

namespace Humans.Budget.Models;

/// <summary>
/// Budget's own badge colours. Base cannot name <see cref="BudgetYearStatus"/> (the enum
/// lives on the section's contracts leaf), and referencing that leaf from
/// <c>Humans.UI</c> to get it back is the trap that ends with Base knowing every section's
/// vocabulary (<c>memory/architecture/base-ui-registries-are-section-populated.md</c>).
/// </summary>
/// <remarks>
/// An extension rather than an <c>EnumBadgeMap.Register</c> row because the three admin
/// views call <c>GetBadgeClass()</c> directly — the map serves
/// <c>CellFormat.EnumBadge</c> table columns, which Budget has none of.
/// </remarks>
internal static class StatusBadgeExtensions
{
    internal static string GetBadgeClass(this BudgetYearStatus status) => status switch
    {
        BudgetYearStatus.Draft => "bg-warning text-dark",
        BudgetYearStatus.Active => "bg-success",
        _ => "bg-secondary"
    };
}
