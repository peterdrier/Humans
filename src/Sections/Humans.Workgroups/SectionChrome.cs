using Humans.Base.Interfaces;

namespace Humans.Workgroups;

/// <summary>
/// The register's card on the Governance page (design §16). Governance renders the slot
/// and names nobody; a Workgroups that is switched off takes its card with it.
/// </summary>
/// <remarks>
/// §16 left the choice open between a new <c>ISectionDashboardTiles</c> seam and a new
/// <see cref="ChromeSlots"/> name. The slot mechanism already does exactly this, so it got
/// a name rather than the interface a new one would have duplicated.
/// </remarks>
internal sealed class SectionChrome : ISectionChrome
{
    public IEnumerable<ChromeComponent> Components() =>
        [new ChromeComponent(ChromeSlots.GovernanceDashboard, "GovernanceWorkgroups", Weight: 20)];
}
