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
    /// Evict the whole cache. For global config changes that shift every holder's
    /// EE at once — the camps' global <c>EeStartDate</c> and EventSettings gate /
    /// build-offset edits (which move every shift-derived date).
    /// </summary>
    void InvalidateAll();
}
