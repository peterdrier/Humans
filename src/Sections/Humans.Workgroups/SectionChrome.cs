using Humans.Base.Interfaces;
using Humans.Workgroups.ViewComponents;

namespace Humans.Workgroups;

internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new ChromeComponent(ChromeSlots.GovernanceDashboard, typeof(GovernanceWorkgroupsViewComponent), Weight: 20)];
}
