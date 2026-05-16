namespace Humans.Application.Interfaces.Camps;

/// <summary>
/// T-06: one-way cross-section signal for the <c>CampInfo</c> read-model cache
/// (design-rules §15e). Implemented by the singleton <c>CachingCampService</c>;
/// callers signal "this camp's data changed, drop or rebuild the cached entry."
/// </summary>
/// <remarks>
/// <para>
/// External call paths that signal this interface:
/// </para>
/// <list type="bullet">
///   <item>The <c>CampInfoSaveChangesInterceptor</c> in Infrastructure — fires
///     <see cref="InvalidateCampAsync"/> for every camp touched by a write to
///     <c>camps</c>, <c>camp_seasons</c>, <c>camp_leads</c>,
///     <c>camp_historical_names</c>, <c>camp_images</c>, or — critically —
///     <c>camp_members</c> (<c>HasEarlyEntry</c> / <c>Status</c> flips affect
///     <see cref="CampSeasonInfo.EeGrantedCount"/>).</item>
///   <item><see cref="InvalidateSettingsAsync"/> rides the same interceptor
///     when <c>camp_settings</c> is touched.</item>
/// </list>
/// <para>
/// The invalidator must resolve to the SAME singleton instance as
/// <see cref="ICampService"/> (per §15e CRITICAL) so the dict and the
/// signaller agree on cache identity.
/// </para>
/// </remarks>
public interface ICampInfoInvalidator
{
    /// <summary>
    /// Refresh-or-evict the cached entry for <paramref name="campId"/>. Safe
    /// to call for ids the cache does not currently hold (no-op).
    /// </summary>
    Task InvalidateCampAsync(Guid campId, CancellationToken ct = default);

    /// <summary>
    /// Drop the cached singleton <c>CampSettingsInfo</c> slot; the next read
    /// rebuilds from <c>ICampRepository</c>.
    /// </summary>
    Task InvalidateSettingsAsync(CancellationToken ct = default);
}
