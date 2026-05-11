using NodaTime;

namespace Humans.Application.DTOs.Calendar;

public sealed record CalendarEventDetail(
    Guid Id,
    string Title,
    string? Description,
    string? Location,
    string? LocationUrl,
    Guid OwningTeamId,
    Instant StartUtc,
    Instant? EndUtc,
    bool IsAllDay,
    string? RecurrenceRule,
    string? RecurrenceTimezone,
    Instant CreatedAt,
    Instant UpdatedAt);
