using NodaTime;

namespace Humans.Calendar.Contracts;

/// <summary>One VEVENT in a member's personal feed.</summary>
/// <remarks>
/// <paramref name="Uid"/> must stay stable across fetches so clients update rather than duplicate,
/// e.g. <c>shift-{signupId}@humans.nobodies.team</c>. <paramref name="Source"/> is the contributing section,
/// emitted as ICS CATEGORIES. <paramref name="Url"/> is app-relative (Calendar resolves it against the public
/// base URL); null when there is no landing page.
/// </remarks>
public sealed record CalendarFeedItem(
    string Uid,
    string Source,
    string Summary,
    string? Description,
    Instant Start,
    Instant End,
    string? Location,
    string? Url);
