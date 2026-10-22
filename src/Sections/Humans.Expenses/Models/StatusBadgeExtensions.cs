using Humans.Expenses.Contracts;

namespace Humans.Expenses.Models;

/// <summary>
/// The single source of badge classes: the views call it, and <c>Section.cs</c> projects it into <c>EnumBadgeMap</c>.
/// </summary>
/// <remarks>
/// The default arm throws on purpose. The projection runs over <c>Enum.GetValues</c> at startup, so a fallback colour
/// would mark every future status as handled and silence <c>EnumBadgeMap.For</c>'s warning. Throwing fails
/// <c>AddSections()</c> instead of shipping a silent grey badge.
/// </remarks>
internal static class StatusBadgeExtensions
{
    internal static string GetBadgeClass(this ExpenseReportStatus status) => status switch
    {
        ExpenseReportStatus.Draft => "bg-secondary",
        ExpenseReportStatus.Submitted => "bg-primary",
        ExpenseReportStatus.CoordinatorEndorsed => "bg-info text-dark",
        ExpenseReportStatus.Approved => "bg-success",
        ExpenseReportStatus.Withdrawn => "bg-secondary",
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "No badge colour is mapped for this expense report status.")
    };
}
