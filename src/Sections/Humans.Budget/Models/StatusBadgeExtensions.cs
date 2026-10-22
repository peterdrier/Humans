using Humans.Budget.Contracts;

namespace Humans.Budget.Models;

/// <summary>
/// Base cannot name <see cref="BudgetYearStatus"/>, and moving the enum into Base would start Base knowing every
/// section's vocabulary. An extension, not an <c>EnumBadgeMap</c> row: the views call <c>GetBadgeClass()</c> directly.
/// </summary>
internal static class StatusBadgeExtensions
{
    internal static string GetBadgeClass(this BudgetYearStatus status) => status switch
    {
        BudgetYearStatus.Draft => "bg-warning text-dark",
        BudgetYearStatus.Active => "bg-success",
        _ => "bg-secondary"
    };
}
