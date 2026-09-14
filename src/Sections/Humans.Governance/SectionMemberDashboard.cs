using Humans.Base.Interfaces;

namespace Humans.Governance;

internal sealed class SectionMemberDashboard : ISectionMemberDashboard
{
    public IEnumerable<ChromeComponent> Components() =>
    [
        new ChromeComponent(ChromeSlots.MemberDashboard, "PendingConsentsAlert", Weight: 5),
        new ChromeComponent(ChromeSlots.MemberDashboard, "MemberTermStatus", Weight: 10),
        new ChromeComponent(ChromeSlots.MemberDashboard, "GovernanceApplicationsTile", Weight: 40),
    ];
}
