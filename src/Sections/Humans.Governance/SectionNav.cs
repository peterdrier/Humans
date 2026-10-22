using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Governance;

/// <summary>Every logged-in member sees it, on a roster or not: they still see what the association is deciding.</summary>
internal sealed class SectionNav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
    [
        new("Nav_Votes", Controller: "GovernanceVotes", Action: "Index",
            Policy: PolicyNames.AppAccess, Weight: 45)
    ];
}
