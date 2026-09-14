using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Budget;

/// <summary>Budget's contribution to the shared "Money" admin group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Money", [
            // "BudgetAdmin", not "Finance": the tag helper resolves controller *names*, not
            // routes ([Route("Finance")] notwithstanding), and a name that resolves to no
            // action renders the anchor with no href at all.
            new("Finance", "BudgetAdmin", "Index", null, null, "fa-solid fa-coins", PolicyNames.FinanceAdminOrAdmin, Weight: 10)
        ], Weight: 50)
    ];
}
