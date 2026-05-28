using Humans.Application.Architecture;

namespace Humans.Application.Interfaces.Caching;

/// <summary>
/// Cross-section signal for the per-user Consent cache (T-04). Implemented by
/// the Singleton <c>CachingConsentService</c> decorator and consumed by the
/// account-merge orchestrator so the surviving target's <c>UserConsentInfo</c>
/// is rebuilt against the post-merge source-id chain.
/// </summary>
/// <remarks>
/// Consent records are append-only per design-rules §12 and stay attributed
/// to the source-tombstone user id after merge (DB triggers reject any
/// rewrite). The cache resolves the chain at warm/refresh time, so a merge
/// requires invalidating <em>both</em> the source and target entries:
/// the target so its consented-version-id set picks up the source's rows,
/// and the source so a stale entry from before tombstoning is discarded.
/// </remarks>
[Grandfathered(
    ruleId: "HUM0028",
    justification: "Pre-existing cross-section flush of consent caches; remains until consent-touching writes route through ConsentService's caching decorator.",
    since: "2026-05-27",
    issueRef: "nobodies-collective/Humans#805")]
public interface IConsentCacheInvalidator : IInvalidator
{
    /// <summary>
    /// Evict the cached <c>UserConsentInfo</c> entry for <paramref name="userId"/>,
    /// if present. The next read for the user lazily refills from the
    /// repository with the current source-id chain applied.
    /// </summary>
    void InvalidateUser(Guid userId);

    /// <summary>
    /// Evict every cached <c>UserConsentInfo</c> entry. Used by paths that
    /// invalidate at coarse granularity (legal-document version add, where
    /// every user's "missing required consent" answer may change because a
    /// new required version exists).
    /// </summary>
    void InvalidateAll();
}
