using Humans.Application.Architecture;

namespace Humans.Application.Interfaces.Camps;

/// <summary>
/// T-06: one-way cross-section signal for the <c>CampInfo</c> read-model cache
/// (design-rules §15e). Implemented by the singleton <c>CachingCampService</c>;
/// callers signal "this camp's data changed, drop or rebuild the cached entry."
/// </summary>
/// <remarks>
/// <para>
/// The decorator implements this interface and calls it from inside every
/// mutating <see cref="ICampService"/> method (after the inner write
/// completes). Cross-table effects — including
/// <c>camp_members.HasEarlyEntry</c> / <c>Status</c> flips that drive
/// <see cref="CampSeasonInfo.EeGrantedCount"/> — are handled by the same
/// in-method invalidation because the mutating method already knows the
/// affected camp id. The no-bypass discipline (only <c>CampService</c> /
/// <c>CampRoleService</c> may use <c>ICampRepository</c>) is pinned by
/// <c>CampsArchitectureTests</c>, so no SaveChanges interceptor backstop is
/// needed.
/// </para>
/// <para>
/// The invalidator must resolve to the SAME singleton instance as
/// <see cref="ICampService"/> (per §15e CRITICAL) so the dict and the
/// signaller agree on cache identity.
/// </para>
/// </remarks>
[Grandfathered(
    ruleId: "HUM0028",
    justification: "Pre-existing camp-info cache flushed cross-section; remains until CampService's caching decorator owns invalidation end-to-end.",
    since: "2026-05-27",
    issueRef: "nobodies-collective/Humans#805")]
public interface ICampInfoInvalidator : IInvalidator
{
    /// <summary>
    /// Refresh-or-evict the cached entry for <paramref name="campId"/>. Safe
    /// to call for ids the cache does not currently hold (no-op).
    /// </summary>
    Task InvalidateCampAsync(Guid campId, CancellationToken ct = default);

    /// <summary>
    /// Refresh-or-evict the cached camp entry that owns <paramref name="campSeasonId"/>.
    /// Use when the mutating path is season-scoped and does not already have the
    /// parent camp id.
    /// </summary>
    Task InvalidateSeasonAsync(Guid campSeasonId, CancellationToken ct = default);

    /// <summary>
    /// Drop the cached singleton <c>CampSettingsInfo</c> slot; the next read
    /// rebuilds from <c>ICampRepository</c>.
    /// </summary>
    Task InvalidateSettingsAsync(CancellationToken ct = default);
}
