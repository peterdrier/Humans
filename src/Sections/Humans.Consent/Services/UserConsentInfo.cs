
namespace Humans.Consent.Services;

/// <summary>
/// Cached per-user projection for the Consent section (T-04). Holds the
/// flat set of <c>DocumentVersionId</c> values the user (and any merged
/// source-id chain-follow tombstones) has explicitly consented to.
/// </summary>
/// <remarks>
/// <para>
/// Footprint: one entry per user, each a small <see cref="HashSet{T}"/>
/// of Guids — well under 1 MB at full population.
/// </para>
/// <para>
/// The chain-follow merge resolution (<see
/// cref="Users.Contracts.IUserServiceRead.GetMergedSourceIdsAsync"/>) is applied at
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
/// observe a stale "still required" entry. The caching decorator refreshes
/// the user's entry (and each merged-source key) via <c>ReplaceAsync</c>
/// before returning from <c>SubmitConsentAsync</c>.
/// </para>
/// </remarks>
/// <param name="UserId">The target user id this entry was keyed under.</param>
/// <param name="ConsentedVersionIds">
/// Document version ids the user has explicitly consented to, unioned
/// across merged-source-id tombstones if any. Read-only set; safe to
/// share across requests.
/// </param>
internal sealed record UserConsentInfo(
    Guid UserId,
    IReadOnlySet<Guid> ConsentedVersionIds);
