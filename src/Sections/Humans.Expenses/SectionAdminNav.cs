using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Expenses;

/// <summary>Expenses' contribution to the shared "Money" admin group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Money", [
            // The review queue serves coordinators and members too and renders in the member
            // shell for them; this entry is the finance-admin door to it, so it keeps the
            // admin policy. Members reach the same page from /Expenses.
            new("Expense review", "Expenses", "Review", null, null, "fa-solid fa-magnifying-glass-dollar", PolicyNames.FinanceAdminOrAdmin, Weight: 0,
                BreadcrumbLabel: "Review")
        ], Weight: 50)
    ];
}
