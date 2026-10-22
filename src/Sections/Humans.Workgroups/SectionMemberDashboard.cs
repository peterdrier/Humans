using Humans.Base.Interfaces;
using Humans.Workgroups.ViewComponents;

namespace Humans.Workgroups;

internal sealed class SectionMemberDashboard : ISectionMemberDashboard
{
    public IEnumerable<ChromeComponent> Components() =>
        [new ChromeComponent(ChromeSlots.MemberDashboard, typeof(MyWorkgroupsViewComponent), Weight: 35)];
}
