using NodaTime;

namespace Humans.Calendar.Contracts;

/// <summary>
/// One VEVENT-shaped item in a user's personal iCal feed.
/// </summary>
/// <param name="Uid">
/// Stable across fetches so calendar clients update rather than duplicate:
/// <c>shift-{signupId}@humans.nobodies.team</c>,
/// <c>event-{eventId}-{occurrenceDate:yyyyMMdd}@humans.nobodies.team</c>,
/// <c>workgroup-meeting-{meetingId}@humans.nobodies.team</c>.
/// </param>
/// <param name="Source">
/// Contributing section ("Shifts", "Events", "Workgroups"). Emitted as ICS CATEGORIES and
/// shown as a badge in the admin widget.
/// </param>
/// <param name="Summary">The calendar item's short display title.</param>
/// <param name="Description">Optional descriptive text for the calendar item.</param>
/// <param name="Start">The item's start time.</param>
/// <param name="End">The item's end time.</param>
/// <param name="Location">Optional location for the calendar item.</param>
/// <param name="Url">
/// App-relative deep link back into the app; Calendar resolves it against the
/// configured public base URL before emitting it as an ICS URL. Null when the
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
    string? Url);
