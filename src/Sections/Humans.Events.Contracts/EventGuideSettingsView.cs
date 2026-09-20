using NodaTime;

namespace Humans.Events.Contracts;

/// <summary>
/// Cached projection of the <c>EventGuideSettings</c> singleton,
/// pre-stitched with <c>TimeZoneId</c> from the foreign <c>EventSettings</c>
/// row so the presentation layer can convert <c>Instant</c> → local time without
/// re-reading the foreign table on every render.
/// </summary>
/// <remarks>
/// <para>
/// Held as a single nullable field inside <c>CachingEventService</c>.
/// </para>
/// <para>
/// <see cref="TimeZoneId"/> is read from the Settings-owned <c>event_settings</c>
/// row at warm / refresh time via <c>ISettingsService</c>. The stale window
/// nobodies-collective/Humans#719 described is closed: <c>CachingEventService</c>
/// implements <c>IEventSettingsChangeListener</c>, so an event-settings save marks
/// this projection stale and the next read reloads it. Before that, a timezone edit
/// showed the old zone until another Events write or a process restart.
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
