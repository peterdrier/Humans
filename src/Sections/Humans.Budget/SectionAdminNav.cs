using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Budget;

/// <summary>Budget's admin sidebar group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Budget", [
            // "BudgetAdmin", not "Finance": the tag helper resolves controller *names*, not
            // routes ([Route("Finance")] notwithstanding), and a name that resolves to no
            // action renders the anchor with no href at all.
            new("Overview", "BudgetAdmin", "Index", null, null, "fa-solid fa-coins", PolicyNames.FinanceAdminOrAdmin)
        ])
    ];
}
