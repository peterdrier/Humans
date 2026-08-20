using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Budget;

/// <summary>Budget's contribution to the shared "Money" admin group (nobodies-collective/Humans#1077).</summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Money", [
            // "BudgetAdmin", not "Finance": A2's controller split (peterdrier/Humans#1239) moved
            // the 23 Budget-CRUD actions — Index among them — out of FinanceController and into
            // BudgetAdminController, which keeps the same [Route("Finance")] prefix. The URL is
            // unchanged; the controller *name* the tag helper resolves against is not, and a
            // name that resolves to no action renders the anchor with no href at all.
            new("Finance", "BudgetAdmin", "Index", null, null, "fa-solid fa-coins", PolicyNames.FinanceAdminOrAdmin, Weight: 10)
        ], Weight: 50)
    ];
}
