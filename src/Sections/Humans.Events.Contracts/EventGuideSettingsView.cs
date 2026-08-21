using NodaTime;

namespace Humans.Events.Contracts;

/// <summary>
/// T-03 — Cached projection of the <c>EventGuideSettings</c> singleton,
/// pre-stitched with <c>TimeZoneId</c> from the foreign <c>EventSettings</c>
/// row so the presentation layer can convert <c>Instant</c> → local time without
/// re-reading the foreign table on every render.
/// </summary>
/// <remarks>
/// <para>
/// Held as a single nullable field inside <c>CachingEventService</c>. Tiny —
/// well under the 50 MB cache budget.
/// </para>
/// <para>
/// <b>Stop-gap stale window (issue #719):</b> <see cref="TimeZoneId"/> is
/// read from the Shifts-owned <c>event_settings</c> table at warm /
/// refresh time via <c>IBurnSettingsService</c>. The Events section has no
/// invalidation signal for burn-settings edits today, so a moderator
/// changing the burn's <c>TimeZoneId</c> will <em>not</em> flush this
/// cache entry until either: (a) another event-section write happens, or
/// (b) the process restarts. Acceptable in practice — <c>TimeZoneId</c>
/// is set per-burn and effectively never changes mid-cycle. Tracked in
/// <see href="https://github.com/nobodies-collective/Humans/issues/719"/>;
/// once <c>IBurnSettingsService</c> exposes an invalidation signal, this
/// section will subscribe and the stale window collapses to zero.
/// </para>
/// </remarks>
public sealed record EventGuideSettingsView(
    Guid Id,
    Guid EventSettingsId,
    Instant SubmissionOpenAt,
    Instant SubmissionCloseAt,
    Instant GuidePublishAt,
    int MaxPrintSlots,
    string? TimeZoneId,
    Instant CreatedAt,
    Instant UpdatedAt)
{
    /// <summary>
    /// Whether <paramref name="now"/> falls within the submission window.
    /// Mirrors <c>EventGuideSettings.IsSubmissionOpenAt</c>.
    /// </summary>
    public bool IsSubmissionOpenAt(Instant now) =>
        now >= SubmissionOpenAt && now <= SubmissionCloseAt;
}
