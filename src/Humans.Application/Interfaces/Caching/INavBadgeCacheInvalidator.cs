namespace Humans.Application.Interfaces.Caching;

/// <summary>
/// Cross-cutting invalidator for the top-nav badge count cache. Owned by
/// whichever section eventually migrates to owning that aggregate; for now,
/// the Infrastructure-resident impl wraps the existing
/// <c>IMemoryCache.InvalidateNavBadgeCounts()</c> extension method so the
/// Governance decorator can surface its dependency visibly.
/// </summary>
public interface INavBadgeCacheInvalidator
{
    void Invalidate();
}
