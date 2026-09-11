using Humans.Base.Interfaces;

namespace Humans.Governance;

internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.AdminDashboard, "TierApplicationsCard", Weight: 30)];
}
