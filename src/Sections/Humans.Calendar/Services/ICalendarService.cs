using Humans.Base.Interfaces;
using Humans.Calendar.Services.Dtos;
using Humans.Calendar.Domain;
using NodaTime;

namespace Humans.Calendar.Services;

/// <summary>
/// Answered only from <see cref="CachingCalendarService"/>'s snapshot, so it is not part of
/// <see cref="ICalendarService"/>: the keyed inner could only implement it with unreachable code.
/// </summary>
internal interface ICalendarQueries : IApplicationService
{
    Task<IReadOnlyList<CalendarOccurrence>> GetOccurrencesInWindowAsync(
        Instant from,
        Instant to,
        DateTimeZone viewerZone,
        Guid? teamId = null,
        CancellationToken ct = default);

    Task<CalendarEventDetail?> GetEventByIdAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Implemented by the keyed inner <c>CalendarService</c> and by the caching decorator.</summary>
internal interface ICalendarService : IApplicationService
{
    Task<IReadOnlyList<CalendarEventInfo>> GetAllEventInfosAsync(CancellationToken ct = default);

    Task<CalendarEventInfo?> GetEventInfoAsync(Guid id, CancellationToken ct = default);


    // Only the result-returning forms are published: a caller catching ValidationException to fill a form field
    // would be doing the service's job.
    Task<CalendarEventMutationResult> CreateEventWithResultAsync(CreateCalendarEventDto dto, Guid createdByUserId, CancellationToken ct = default);

    Task<CalendarEventMutationResult> UpdateEventWithResultAsync(Guid id, CreateCalendarEventDto dto, Guid updatedByUserId, CancellationToken ct = default);

    Task DeleteEventAsync(Guid id, Guid deletedByUserId, CancellationToken ct = default);

    /// <summary>Returns false for a missing series or invalid occurrence identity; dependency faults propagate.</summary>
    Task<bool> CancelOccurrenceAsync(Guid eventId, Instant? originalOccurrenceStartUtc, Guid userId, CancellationToken ct = default, LocalDate? originalDate = null);

    /// <summary>Returns false for a missing series or invalid occurrence override; dependency faults propagate.</summary>
    Task<bool> OverrideOccurrenceAsync(Guid eventId, Instant? originalOccurrenceStartUtc, OverrideOccurrenceDto dto, Guid userId, CancellationToken ct = default, LocalDate? originalDate = null);
}

internal sealed record CalendarEventMutationResult(
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
