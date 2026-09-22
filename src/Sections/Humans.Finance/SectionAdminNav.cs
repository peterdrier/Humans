using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Finance;

/// <summary>Finance's admin sidebar group (nobodies-collective/Humans#1077).</summary>
/// <remarks>
/// One link, deliberately: <c>/Finance/Holded</c> is the index for this section's Holded surface and
/// carries the way into HoldedAccounts, HoldedUnmatched and Creditors, which had no sidebar entry of
/// their own.
/// </remarks>
internal sealed class SectionAdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Finance", [
            new("Holded connector", "Finance", "Holded", null, null, "fa-solid fa-plug", PolicyNames.FinanceAdminOrAdmin)
        ])
    ];
}
