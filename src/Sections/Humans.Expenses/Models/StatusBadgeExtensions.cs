using Humans.Expenses.Contracts;

namespace Humans.Expenses.Models;

/// <summary>
/// Badge CSS classes for <see cref="ExpenseReportStatus"/>. The single source: the section's
/// Detail and Review pages call it directly, and <c>Section.cs</c> projects it into
/// <c>EnumBadgeMap</c> for the table-column half of the same page.
/// </summary>
/// <remarks>
/// It lives in the section rather than Base because Base cannot name a section enum
/// (memory/architecture/base-ui-registries-are-section-populated.md).
/// </remarks>
internal static class StatusBadgeExtensions
{
    internal static string GetBadgeClass(this ExpenseReportStatus status) => status switch
    {
        ExpenseReportStatus.Draft => "bg-secondary",
        ExpenseReportStatus.Submitted => "bg-primary",
        ExpenseReportStatus.CoordinatorEndorsed => "bg-info text-dark",
        ExpenseReportStatus.Approved => "bg-success",
        _ => "bg-secondary"
    };
}
