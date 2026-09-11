using NodaTime;

namespace Humans.Calendar.Services.Dtos;

/// <summary>
/// One item rendered on the community calendar's grid/list/agenda — either a
/// Calendar-owned <c>calendar_events</c> occurrence, or a public item merged in from an
/// <see cref="Humans.Calendar.Contracts.ICalendarFeedContributor"/> via
/// <c>Source</c>/<c>Url</c>. Contributed items carry <c>Guid.Empty</c> for
/// <see cref="EventId"/> and <see cref="OwningTeamId"/> — they don't belong to a Calendar
/// event or a team — so views route their title through <see cref="Url"/> instead of
/// <c>/Calendar/Event/{id}</c>.
/// </summary>
internal sealed record CalendarOccurrence(
    Guid EventId,
    Instant OccurrenceStartUtc,
    Instant? OccurrenceEndUtc,
    bool IsAllDay,
    string Title,
    string? Description,
    string? Location,
    string? LocationUrl,
    Guid OwningTeamId,
    string OwningTeamName,
    bool IsRecurring,
    Instant? OriginalOccurrenceStartUtc,
    string Source = CalendarOccurrence.CalendarSource,
    string? Url = null)
{
    /// <summary>Source value for Calendar's own <c>calendar_events</c> occurrences.</summary>
    public const string CalendarSource = "Calendar";
}
