using Humans.Base.Interfaces;

namespace Humans.Governance;

/// <summary>
/// Governance's member-dashboard content: the pending-consents nudge, the
/// Colaborador/Asociado term card, the open-assembly-vote card, and the tier-application
/// navigation tile.
/// </summary>
internal sealed class SectionMemberDashboard : ISectionMemberDashboard
{
    public IEnumerable<ChromeComponent> Components() =>
    [
        new ChromeComponent(ChromeSlots.MemberDashboard, "PendingConsentsAlert", Weight: 5),
        new ChromeComponent(ChromeSlots.MemberDashboard, "MemberTermStatus", Weight: 10),
        new ChromeComponent(ChromeSlots.MemberDashboard, "AssemblyVotesCard", Weight: 15),
        new ChromeComponent(ChromeSlots.MemberDashboard, "GovernanceApplicationsTile", Weight: 40),
    ];
}
