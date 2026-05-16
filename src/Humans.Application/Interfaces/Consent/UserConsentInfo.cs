namespace Humans.Application.Interfaces.Consent;

/// <summary>
/// Cached per-user projection for the Consent section (T-04). Holds the
/// flat set of <c>DocumentVersionId</c> values the user (and any merged
/// source-id chain-follow tombstones) has explicitly consented to.
/// </summary>
/// <remarks>
/// <para>
/// Footprint budget (CLAUDE.md "Scale and Deployment"): one entry per
/// user (~500 entries at full population), each entry a small
/// <see cref="HashSet{T}"/> of Guids (the user's consented version ids,
/// typically a handful). Worst-case &lt; 1 MB at full population —
/// well within the 50 MB per-projection budget the cache-migration spec
/// allots.
/// </para>
/// <para>
/// The chain-follow merge resolution (<see
/// cref="Users.IUserService.GetMergedSourceIdsAsync"/>) is applied at
/// warm/refresh time, not at read time — every cache entry already
/// represents the union of the target user's explicit consents plus
/// those of any merged source tombstones. Invalidation must trigger on
/// account merge accept so the surviving target's entry is rebuilt
/// against the new chain.
/// </para>
/// <para>
/// Synchronous invalidation on <see
/// cref="IConsentService.SubmitConsentAsync"/> is the load-bearing
/// invariant of this cache: the controller redirects immediately after
/// the submit returns, and the next-page consent-banner check must not
/// observe a stale "still required" entry. The caching decorator clears
/// the user's entry before returning from <c>SubmitConsentAsync</c>.
/// </para>
/// </remarks>
/// <param name="UserId">The target user id this entry was keyed under.</param>
/// <param name="ConsentedVersionIds">
/// Document version ids the user has explicitly consented to, unioned
/// across merged-source-id tombstones if any. Read-only set; safe to
/// share across requests.
/// </param>
public sealed record UserConsentInfo(
    Guid UserId,
    IReadOnlySet<Guid> ConsentedVersionIds);
