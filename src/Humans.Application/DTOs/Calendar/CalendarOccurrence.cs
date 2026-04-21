using NodaTime;

namespace Humans.Application.DTOs.Calendar;

public sealed record CalendarOccurrence(
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
    Instant? OriginalOccurrenceStartUtc);
