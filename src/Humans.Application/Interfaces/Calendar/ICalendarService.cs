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

    Task<CalendarEventDetail?> GetEventByIdAsync(Guid id, CancellationToken ct = default);

    Task<CalendarEvent> CreateEventAsync(CreateCalendarEventDto dto, Guid createdByUserId, CancellationToken ct = default);

    Task<CalendarEventMutationResult> CreateEventWithResultAsync(CreateCalendarEventDto dto, Guid createdByUserId, CancellationToken ct = default);

    Task<CalendarEvent> UpdateEventAsync(Guid id, UpdateCalendarEventDto dto, Guid updatedByUserId, CancellationToken ct = default);

    Task<CalendarEventMutationResult> UpdateEventWithResultAsync(Guid id, UpdateCalendarEventDto dto, Guid updatedByUserId, CancellationToken ct = default);

    Task DeleteEventAsync(Guid id, Guid deletedByUserId, CancellationToken ct = default);

    Task CancelOccurrenceAsync(Guid eventId, Instant originalOccurrenceStartUtc, Guid userId, CancellationToken ct = default);

    Task OverrideOccurrenceAsync(Guid eventId, Instant originalOccurrenceStartUtc, OverrideOccurrenceDto dto, Guid userId, CancellationToken ct = default);
}

public sealed record CalendarEventMutationResult(
    bool Succeeded,
    bool NotFound,
    CalendarEvent? Event,
    string? ValidationMemberName,
    string? ErrorMessage)
{
    public static CalendarEventMutationResult Success(CalendarEvent ev) => new(true, false, ev, null, null);

    public static CalendarEventMutationResult Missing(string message) => new(false, true, null, null, message);

    public static CalendarEventMutationResult ValidationFailed(string memberName, string message) =>
        new(false, false, null, memberName, message);

    public static CalendarEventMutationResult Failed(string message) => new(false, false, null, null, message);
}
