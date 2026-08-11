using NodaTime;

namespace Humans.Calendar.Services.Dtos;

internal sealed record CreateCalendarEventDto(
    string Title,
    string? Description,
    string? Location,
    string? LocationUrl,
    Guid OwningTeamId,
    Instant StartUtc,
    Instant? EndUtc,
    bool IsAllDay,
    string? RecurrenceRule,
    string? RecurrenceTimezone);
