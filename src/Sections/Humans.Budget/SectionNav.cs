using Humans.Base.Interfaces;
using Humans.Base.Authorization;

namespace Humans.Budget;

/// <summary>
/// Member top-nav contribution. "Budget" is a literal string, not a resource key,
/// and stays literal (a key with no entry renders as itself).
/// </summary>
internal sealed class SectionNav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
        [new("Budget", Controller: "Budget", Action: "Summary", Policy: PolicyNames.AppAccess, Weight: 70)];
}
