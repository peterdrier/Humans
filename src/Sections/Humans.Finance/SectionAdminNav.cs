using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Finance;

/// <summary>
/// One link on purpose: <c>/Finance/Holded</c> is the index that leads to HoldedAccounts, HoldedUnmatched and
/// Creditors.
/// </summary>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Finance", [
            new("Holded connector", "Finance", "Holded", null, null, "fa-solid fa-plug", PolicyNames.FinanceAdminOrAdmin)
        ])
    ];
}
