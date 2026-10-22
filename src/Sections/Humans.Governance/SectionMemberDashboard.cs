using Humans.Base.Interfaces;
using Humans.Governance.ViewComponents;

namespace Humans.Governance;

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
