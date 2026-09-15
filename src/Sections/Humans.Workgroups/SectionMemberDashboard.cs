using Humans.Base.Interfaces;

namespace Humans.Workgroups;

/// <summary>"My workgroups" on the member dashboard: the groups this person belongs to.</summary>
internal sealed class SectionMemberDashboard : ISectionMemberDashboard
{
    public IEnumerable<ChromeComponent> Components() =>
        [new ChromeComponent(ChromeSlots.MemberDashboard, "MyWorkgroups", Weight: 35)];
}
