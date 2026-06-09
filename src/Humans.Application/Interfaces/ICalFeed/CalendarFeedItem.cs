using NodaTime;

namespace Humans.Application.Interfaces.ICalFeed;

/// <summary>
/// One VEVENT-shaped item in a user's personal iCal feed.
/// </summary>
/// <param name="Uid">
/// Stable across fetches so calendar clients update rather than duplicate:
/// <c>shift-{signupId}@humans.nobodies.team</c>,
/// <c>event-{eventId}-{occurrenceDate:yyyyMMdd}@humans.nobodies.team</c>.
/// </param>
/// <param name="Source">
/// Contributing section ("Shifts", "Events"). Emitted as ICS CATEGORIES and
/// shown as a badge in the admin widget.
/// </param>
/// <param name="Url">
/// Absolute deep link back into the app; emitted as ICS URL. Null when the
/// contributor has no sensible landing page for the item.
/// </param>
public sealed record CalendarFeedItem(
    string Uid,
    string Source,
    string Summary,
    string? Description,
    Instant Start,
    Instant End,
    string? Location,
    string? Url)
{
    /// <summary>
    /// Hardcoded production base for feed deep links — won't change before the
    /// 2026 event. TODO(2027): move to configuration.
    /// </summary>
    public const string BaseUrl = "https://humans.nobodies.team";
}
