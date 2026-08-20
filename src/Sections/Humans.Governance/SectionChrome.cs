using Humans.Base.Interfaces;

namespace Humans.Governance;

/// <summary>Layout chrome contribution — the tier-applications card on the admin dashboard.</summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new(ChromeSlots.AdminDashboard, "TierApplicationsCard", Weight: 30)];
}
