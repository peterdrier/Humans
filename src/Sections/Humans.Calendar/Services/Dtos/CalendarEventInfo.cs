using NodaTime;

namespace Humans.Calendar.Services.Dtos;

/// <summary>
/// Immutable projection of a <see cref="Humans.Calendar.Domain.CalendarEvent"/>
/// row, embedding its per-occurrence <see cref="Exceptions"/> collection.
/// Owned by the caching decorator; cache key is <see cref="Id"/>.
/// </summary>
/// <remarks>
/// <para>
/// Cache shape: the decorator holds <em>all</em> non-soft-deleted events keyed
/// by id. Window queries (<c>GetOccurrencesInWindowAsync</c>) snapshot-scan
/// this dict and filter by date bounds for all-day series or instant bounds for timed
/// series. Rows with exceptions survive the prefilter because overrides can move outside it.
/// Expansion + exception merging stay in the service layer
/// (<see cref="CalendarOccurrenceExpander"/>).
/// </para>
/// <para>
/// Exception writes (<c>CancelOccurrenceAsync</c> / <c>OverrideOccurrenceAsync</c>)
/// upsert into the <c>calendar_event_exceptions</c> child table but the cache
/// is keyed by the <em>parent</em> event id — these writes evict the parent
/// <see cref="CalendarEventInfo"/> entry, NOT a separate exception row. The
/// next read repopulates the parent (with its refreshed <see cref="Exceptions"/>
/// list) through <see cref="ICalendarService.GetEventInfoAsync"/>.
/// </para>
/// <para>
/// The projection must stay well under the §15 50 MB-per-projection budget.
/// </para>
/// </remarks>
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

/// <summary>
/// Immutable projection of a single <c>calendar_event_exceptions</c> row,
/// carried inside <see cref="CalendarEventInfo.Exceptions"/>. Never cached
/// independently — the parent event is the eviction unit.
/// </summary>
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
