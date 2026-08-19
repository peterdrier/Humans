using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Finance;

/// <summary>Finance's contribution to the shared "Money" admin group (nobodies-collective/Humans#1077).</summary>
/// <remarks>
/// One link, deliberately: <c>/Finance/Holded</c> is the index for this section's Holded surface and
/// carries the way into HoldedAccounts, HoldedUnmatched and Creditors, which had no sidebar entry of
/// their own. It sits just below the mirror's own <c>/Holded</c> screen (weight 20).
/// </remarks>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Money", [
            new("Holded connector", "Finance", "Holded", null, null, "fa-solid fa-plug", PolicyNames.FinanceAdminOrAdmin, Weight: 25)
        ], Weight: 50)
    ];
}
