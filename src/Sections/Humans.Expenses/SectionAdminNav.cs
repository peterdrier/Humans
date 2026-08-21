using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Expenses;

/// <summary>Expenses' contribution to the shared "Money" admin group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Money", [
            // Members' own expense pages (Index/Coordinator) are member-shell pages
            // linked from the member nav — only the finance review queue is admin.
            new("Expense review", "Expenses", "Review", null, null, "fa-solid fa-magnifying-glass-dollar", PolicyNames.FinanceAdminOrAdmin, Weight: 0),
            // The organisation's liabilities to vendors, not any member's expenses
            // (nobodies-collective/Humans#1030).
            new("Vendor commitments", "Commitments", "Index", null, null, "fa-solid fa-file-signature", PolicyNames.FinanceAdminOrAdmin, Weight: 1)
        ], Weight: 50)
    ];
}
