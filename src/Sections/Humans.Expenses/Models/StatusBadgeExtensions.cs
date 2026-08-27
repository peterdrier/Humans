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
/// <para>
/// <b>The default arm throws, deliberately.</b> Because <c>Section.Register</c> projects this
/// over <c>Enum.GetValues</c>, a fallback <em>colour</em> here would register every future
/// status as handled, and <c>EnumBadgeMap.For</c>'s unhandled-value warning — the only thing
/// that says a status was missed — would never fire again. Throwing instead moves that signal
/// earlier and makes it louder: the projection runs at composition time, so adding a member to
/// <see cref="ExpenseReportStatus"/> without a colour fails <c>AddSections()</c> at startup
/// rather than shipping a silent grey badge. A named member must be listed below.
/// </para>
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
