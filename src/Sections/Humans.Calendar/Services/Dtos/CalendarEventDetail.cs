using NodaTime;

namespace Humans.Calendar.Services.Dtos;

internal sealed record CalendarEventDetail(
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
