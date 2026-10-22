using NodaTime;

namespace Humans.Calendar.Services.Dtos;

/// <summary>
/// Cached by event id. Exception writes refresh the whole parent entry: exceptions have no cache row of their own.
/// Rows with exceptions survive the window prefilter, because overrides can move outside it.
/// </summary>
internal sealed record CalendarEventInfo(
    Guid Id,
    string Title,
    string? Description,
    string? Location,
    string? LocationUrl,
    Guid OwningTeamId,
    Instant? StartUtc,
    Instant? EndUtc,
    bool IsAllDay,
    string? RecurrenceRule,
    string? RecurrenceTimezone,
    Instant? RecurrenceUntilUtc,
    Guid CreatedByUserId,
    Instant CreatedAt,
    Instant UpdatedAt,
    IReadOnlyList<CalendarEventExceptionInfo> Exceptions,
    LocalDate? StartDate = null,
    LocalDate? EndDateExclusive = null,
    LocalDate? RecurrenceUntilDate = null);

internal sealed record CalendarEventExceptionInfo(
    Guid Id,
    Instant? OriginalOccurrenceStartUtc,
    bool IsCancelled,
    Instant? OverrideStartUtc,
    Instant? OverrideEndUtc,
    string? OverrideTitle,
    string? OverrideDescription,
    string? OverrideLocation,
    string? OverrideLocationUrl,
    LocalDate? OriginalOccurrenceDate = null,
    LocalDate? OverrideStartDate = null,
    LocalDate? OverrideEndDateExclusive = null);
