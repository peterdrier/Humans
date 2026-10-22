using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Budget;

internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Budget", [
            // "BudgetAdmin", not "Finance": the tag helper resolves controller names, not routes, and a name that
            // resolves to no action renders the anchor with no href.
            new("Overview", "BudgetAdmin", "Index", null, null, "fa-solid fa-coins", PolicyNames.FinanceAdminOrAdmin)
        ])
    ];
}
