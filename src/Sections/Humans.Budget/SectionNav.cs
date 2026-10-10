using Humans.Base.Interfaces;
using Humans.Base.Authorization;

namespace Humans.Budget;

/// <summary>Member top-nav contribution.</summary>
internal sealed class SectionNav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
        [new("Nav_Budget", Controller: "Budget", Action: "Summary", Policy: PolicyNames.AppAccess, Weight: 70)];
}
