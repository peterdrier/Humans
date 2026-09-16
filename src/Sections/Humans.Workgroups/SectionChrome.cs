using Humans.Base.Interfaces;

namespace Humans.Workgroups;

/// <summary>
/// The register's card on the Governance page (design §16). Governance renders the slot
/// and names nobody; a Workgroups that is switched off takes its card with it.
/// </summary>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new ChromeComponent(ChromeSlots.GovernanceDashboard, "GovernanceWorkgroups", Weight: 20)];
}
