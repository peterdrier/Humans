using Humans.Base.Attributes;
using Humans.Base.Interfaces;

namespace Humans.EarlyEntry.Contracts;

/// <summary>
/// Contributors tell this section to forget a cached answer after they write
/// (design-rules §15e). Camps, Shifts and Teams call it.
/// </summary>
[Grandfathered(
    ruleId: "HUM0028",
    justification: "Pre-existing early-entry cache flushed by section providers; remains until EarlyEntryService's caching decorator owns invalidation end-to-end.",
    since: "2026-05-27",
    issueRef: "nobodies-collective/Humans#805")]
public interface IEarlyEntryInvalidator : IInvalidator
{
    /// <summary>Forget one person's answer.</summary>
    void InvalidateUser(Guid userId);

    /// <summary>
    /// Evict the whole cache after a capability change or aggregate removal (camp, team,
    /// shift event), including uncertain deletion completion. A team's <c>EarlyEntryEnabled</c> flip also uses this. Event-settings saves reach the same eviction through
    /// the cache's <c>IEventSettingsChangeListener</c>.
    /// </summary>
    void InvalidateAll();
}
