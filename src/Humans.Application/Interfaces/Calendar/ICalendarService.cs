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

    Task<CalendarMutationResult> MutateCalendarAsync(
        CalendarMutation mutation,
        Guid actorUserId,
        CancellationToken ct = default);
}

public abstract record CalendarMutation;

public sealed record CreateCalendarEventMutation(
    CreateCalendarEventDto Event) : CalendarMutation;

public sealed record UpdateCalendarEventMutation(
    Guid EventId,
    UpdateCalendarEventDto Event) : CalendarMutation;

public sealed record DeleteCalendarEventMutation(
    Guid EventId) : CalendarMutation;

public sealed record CancelCalendarOccurrenceMutation(
    Guid EventId,
    Instant OriginalOccurrenceStartUtc) : CalendarMutation;

public sealed record OverrideCalendarOccurrenceMutation(
    Guid EventId,
    Instant OriginalOccurrenceStartUtc,
    OverrideOccurrenceDto Override) : CalendarMutation;

public sealed record CalendarMutationResult(
    bool Succeeded,
    bool NotFound,
    CalendarEvent? Event,
    Guid? AffectedEventId,
    string? ValidationMemberName,
    string? ErrorMessage)
{
    public static CalendarMutationResult Success(CalendarEvent ev) =>
        new(true, false, ev, ev.Id, null, null);

    public static CalendarMutationResult Success(Guid affectedEventId) =>
        new(true, false, null, affectedEventId, null, null);

    public static CalendarMutationResult Missing(Guid affectedEventId, string message) =>
        new(false, true, null, affectedEventId, null, message);

    public static CalendarMutationResult ValidationFailed(string memberName, string message) =>
        new(false, false, null, null, memberName, message);

    public static CalendarMutationResult Failed(string message) =>
        new(false, false, null, null, null, message);
}
