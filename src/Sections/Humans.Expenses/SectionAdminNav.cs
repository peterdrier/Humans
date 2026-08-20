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
            new("Expense review", "Expenses", "Review", null, null, "fa-solid fa-magnifying-glass-dollar", PolicyNames.FinanceAdminOrAdmin, Weight: 0)
        ], Weight: 50)
    ];
}
