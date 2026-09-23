using Humans.Base.Interfaces;
using Humans.Governance.ViewComponents;

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
        new ChromeComponent(ChromeSlots.MemberDashboard, typeof(PendingConsentsAlertViewComponent), Weight: 5),
        new ChromeComponent(ChromeSlots.MemberDashboard, typeof(MemberTermStatusViewComponent), Weight: 10),
        new ChromeComponent(ChromeSlots.MemberDashboard, typeof(AssemblyVotesCardViewComponent), Weight: 15),
        new ChromeComponent(ChromeSlots.MemberDashboard, typeof(GovernanceApplicationsTileViewComponent), Weight: 40),
    ];
}
