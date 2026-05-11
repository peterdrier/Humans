using Humans.Application.Interfaces;
using Humans.Application.DTOs.Calendar;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces.Calendar;

public interface ICalendarService : IApplicationService
{
    Task<IReadOnlyList<CalendarOccurrence>> GetOccurrencesInWindowAsync(
        Instant from,
        Instant to,
        Guid? teamId = null,
        CancellationToken ct = default);

    Task<CalendarEventInfo?> GetEventByIdAsync(Guid id, CancellationToken ct = default);

    Task<CalendarEvent> CreateEventAsync(CreateCalendarEventDto dto, Guid createdByUserId, CancellationToken ct = default);

    Task<CalendarEvent> UpdateEventAsync(Guid id, UpdateCalendarEventDto dto, Guid updatedByUserId, CancellationToken ct = default);

    Task DeleteEventAsync(Guid id, Guid deletedByUserId, CancellationToken ct = default);

    Task CancelOccurrenceAsync(Guid eventId, Instant originalOccurrenceStartUtc, Guid userId, CancellationToken ct = default);

    Task OverrideOccurrenceAsync(Guid eventId, Instant originalOccurrenceStartUtc, OverrideOccurrenceDto dto, Guid userId, CancellationToken ct = default);
}

public sealed record CalendarEventInfo(
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
    Instant? RecurrenceUntilUtc,
    Guid CreatedByUserId,
    Instant CreatedAt,
    Instant UpdatedAt);
