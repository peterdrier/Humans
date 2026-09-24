using Humans.Base.Interfaces;
using Humans.Governance.ViewComponents;

namespace Humans.Governance;

internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [
            new(ChromeSlots.AdminDashboard, typeof(TierApplicationsCardViewComponent), Weight: 30),
        ];
}
