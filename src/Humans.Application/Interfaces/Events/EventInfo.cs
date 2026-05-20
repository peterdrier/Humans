using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Events;

/// <summary>
/// T-03 — Cached projection of a single approved <see cref="Event"/> row,
/// flattened with its <see cref="EventCategory"/> and <see cref="EventVenue"/>
/// fields so the public guide / API can render without joining at read time.
/// </summary>
/// <remarks>
/// <para>
/// Held in a <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
/// keyed by <see cref="Id"/> inside <c>CachingEventService</c>. Only events in
/// <see cref="EventStatus.Approved"/> are projected — the moderation dashboard
/// (which needs the live pending count) reads direct DB via
/// <c>GetAllEventsForDashboardAsync</c>. Cache size at the expected ~500
/// approved events × ~2 KB per row ≈ 1 MB — well under the 50 MB budget.
/// </para>
/// <para>
/// Sub-property records embed the joined-in category and venue so consumers
/// don't need to look them up separately at read time. Both are pre-stitched
/// at warm/refresh time from the in-memory category + venue tables.
/// </para>
/// </remarks>
public sealed record ApprovedEventView(
    Guid Id,
    Guid? CampId,
    Guid? GuideSharedVenueId,
    Guid SubmitterUserId,
    Guid CategoryId,
    string CategorySlug,
    string CategoryName,
    bool CategoryIsSensitive,
    string? VenueName,
    string Title,
    string Description,
    string? LocationNote,
    string? Host,
    Instant StartAt,
    int DurationMinutes,
    bool IsRecurring,
    string? RecurrenceDays,
    int PriorityRank,
    Instant SubmittedAt,
    Instant LastUpdatedAt);

/// <summary>
/// T-03 — Cached projection of an <see cref="EventCategory"/> row.
/// </summary>
/// <remarks>
/// Held as a flat <see cref="IReadOnlyList{T}"/> inside
/// <c>CachingEventService</c>. ~10–30 categories × ~100 bytes ≈ trivial.
/// </remarks>
public sealed record EventCategoryView(
    Guid Id,
    string Name,
    string Slug,
    bool IsSensitive,
    int DisplayOrder,
    bool IsActive);

/// <summary>
/// T-03 — Cached projection of an <see cref="EventVenue"/> row.
/// </summary>
/// <remarks>
/// Held as a flat <see cref="IReadOnlyList{T}"/> inside
/// <c>CachingEventService</c>. ~10–30 venues × ~200 bytes ≈ trivial.
/// </remarks>
public sealed record EventVenueView(
    Guid Id,
    string Name,
    string? Description,
    string? LocationDescription,
    int DisplayOrder,
    bool IsActive);

/// <summary>
/// T-03 — Cached projection of the <see cref="EventGuideSettings"/> singleton,
/// pre-stitched with <c>TimeZoneId</c> from the foreign <see cref="EventSettings"/>
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
    /// Mirrors <see cref="EventGuideSettings.IsSubmissionOpenAt"/>.
    /// </summary>
    public bool IsSubmissionOpenAt(Instant now) =>
        now >= SubmissionOpenAt && now <= SubmissionCloseAt;
}
