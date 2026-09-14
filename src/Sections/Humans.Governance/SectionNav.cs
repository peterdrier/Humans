using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Governance;

/// <summary>
/// Governance's member top-nav contribution: the assembly votes list. Every logged-in member
/// sees it whether or not they are on any roster — non-roster members still see what the
/// association is deciding (Docs/features/assembly-votes.md, decision 8).
/// </summary>
internal sealed class SectionNav : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() =>
    [
        new("Nav_Votes", Controller: "GovernanceVotes", Action: "Index",
            Policy: PolicyNames.AppAccess, Weight: 45)
    ];
}
