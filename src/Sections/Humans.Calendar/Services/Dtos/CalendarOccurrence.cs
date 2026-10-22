using NodaTime;

namespace Humans.Calendar.Services.Dtos;

/// <summary>
/// Contributed items carry <c>Guid.Empty</c> for <see cref="EventId"/> and <see cref="OwningTeamId"/>, so views
/// link them through <see cref="Url"/> instead of <c>/Calendar/Event/{id}</c>.
/// </summary>
internal sealed record CalendarOccurrence(
    Guid EventId,
    Instant? OccurrenceStartUtc,
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
    LocalDate? StartDate = null,
    LocalDate? EndDateExclusive = null,
    LocalDate? OriginalOccurrenceDate = null,
    string Source = CalendarOccurrence.CalendarSource,
    string? Url = null)
{
    public const string CalendarSource = "Calendar";
}
