using Humans.Expenses.Contracts;

namespace Humans.Expenses.Models;

/// <summary>
/// Badge CSS classes for <see cref="ExpenseReportStatus"/>, used by the section's own
/// Detail and Review pages.
/// </summary>
/// <remarks>
/// This lived as an overload on <c>Humans.Base.Extensions.StatusBadgeExtensions</c> until the
/// section moved out. Its only two call sites were always this section's views, so it comes
/// with them rather than making Base name a section enum — the same reasoning as
/// <c>EnumBadgeMap.Register</c>, which covers the table-column half of the same page.
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
