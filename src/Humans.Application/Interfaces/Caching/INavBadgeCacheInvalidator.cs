using Humans.Application.Architecture;

namespace Humans.Application.Interfaces.Caching;

/// <summary>
/// Cross-cutting invalidator for the top-nav badge count cache. Owned by
/// whichever section eventually migrates to owning that aggregate; for now,
/// the Infrastructure-resident impl wraps the existing
/// <c>IMemoryCache.InvalidateNavBadgeCounts()</c> extension method so the
/// Governance decorator can surface its dependency visibly.
/// </summary>
[Grandfathered(
    ruleId: "HUM0028",
    justification: "Pre-existing aggregate nav-badge cache flushed across sections; remains until the per-badge service-owned caches absorb invalidation.",
    since: "2026-05-27",
    issueRef: "nobodies-collective/Humans#805")]
public interface INavBadgeCacheInvalidator : IInvalidator
{
    void Invalidate();
}
