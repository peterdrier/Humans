using Humans.Base.Interfaces;
using Humans.Calendar.Services.Dtos;
using Humans.Calendar.Domain;
using NodaTime;

namespace Humans.Calendar.Services;

/// <summary>
/// The section's read surface. Both members are answered from
/// <see cref="CachingCalendarService"/>'s in-memory snapshot and never reach SQL, so this
/// contract is deliberately not part of <see cref="ICalendarService"/>: the keyed inner
/// service would only be able to implement it with code nothing can reach.
/// </summary>
internal interface ICalendarServiceRead : IApplicationService
{
    Task<IReadOnlyList<CalendarOccurrence>> GetOccurrencesInWindowAsync(
        Instant from,
        Instant to,
        Guid? teamId = null,
        CancellationToken ct = default);

    Task<CalendarEventDetail?> GetEventByIdAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// The mutation surface, plus the two row loads the cache warms and refreshes from.
/// Implemented twice: by <c>CalendarService</c> (the keyed inner, which does the work) and
/// by <see cref="CachingCalendarService"/> (which delegates and then refreshes its snapshot).
/// </summary>
internal interface ICalendarService : IApplicationService
{
    Task<IReadOnlyList<CalendarEventInfo>> GetAllEventInfosAsync(CancellationToken ct = default);

    Task<CalendarEventInfo?> GetEventInfoAsync(Guid id, CancellationToken ct = default);


    // Create and update are published only in their result-returning form. The throwing pair
    // they wrap stays private to CalendarService: a caller that has to catch ValidationException
    // to render a form field is a caller doing the service's job.
    Task<CalendarEventMutationResult> CreateEventWithResultAsync(CreateCalendarEventDto dto, Guid createdByUserId, CancellationToken ct = default);

    Task<CalendarEventMutationResult> UpdateEventWithResultAsync(Guid id, UpdateCalendarEventDto dto, Guid updatedByUserId, CancellationToken ct = default);

    Task DeleteEventAsync(Guid id, Guid deletedByUserId, CancellationToken ct = default);

    Task CancelOccurrenceAsync(Guid eventId, Instant? originalOccurrenceStartUtc, Guid userId, CancellationToken ct = default, LocalDate? originalDate = null);

    Task OverrideOccurrenceAsync(Guid eventId, Instant? originalOccurrenceStartUtc, OverrideOccurrenceDto dto, Guid userId, CancellationToken ct = default, LocalDate? originalDate = null);
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
