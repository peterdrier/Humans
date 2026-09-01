using Humans.Base.Interfaces.Repositories;
using Humans.Calendar.Domain;
using NodaTime;

namespace Humans.Calendar.Data;

/// <summary>
/// Repository for the Calendar section's <c>calendar_events</c> and
/// <c>calendar_event_exceptions</c> tables. The only non-test file that
/// touches <c>DbContext.CalendarEvents</c> / <c>DbContext.CalendarEventExceptions</c>.
/// </summary>
/// <remarks>
/// Entities-in / entities-out per design-rules §3. Read methods are
/// <c>AsNoTracking</c>. Mutating methods load tracked entities and save
/// changes atomically inside a single
/// <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{TContext}"/>-owned
/// context so callers never have to reason about the EF context lifetime.
/// The owning team is a bare Guid with no navigation property, so team display
/// names are stitched in memory by the application service, never joined in SQL.
/// </remarks>
internal interface ICalendarRepository : IRepository
{
    /// <summary>
    /// Returns calendar events whose window overlaps with
    /// <c>[from, to]</c> — i.e. events that start before <paramref name="to"/>
    /// and whose recurrence end (or <c>null</c>, meaning open-ended) is on or
    /// after <paramref name="from"/>. Optionally filtered by team.
    /// Exceptions are loaded eagerly. Soft-deleted rows are filtered via the
    /// global query filter. Read-only (<c>AsNoTracking</c>).
    /// </summary>
    Task<IReadOnlyList<CalendarEvent>> GetEventsInWindowAsync(
        Instant from,
        Instant to,
        Guid? teamId,
        CancellationToken ct = default);

    /// <summary>
    /// Loads a single <see cref="CalendarEvent"/> by id, with its
    /// <c>Exceptions</c> collection included. Returns <c>null</c> if not
    /// found or soft-deleted. Read-only (<c>AsNoTracking</c>).
    /// </summary>
    Task<CalendarEvent?> GetEventByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Returns every non-soft-deleted <see cref="CalendarEvent"/>, with its
    /// <c>Exceptions</c> collection included. Used by the Calendar caching
    /// decorator's warmup path — the cache holds all events keyed by id and
    /// answers window queries via in-memory snapshot scan. Read-only
    /// (<c>AsNoTracking</c>).
    /// </summary>
    Task<IReadOnlyList<CalendarEvent>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// The caller is responsible for validating the entity first.
    /// </summary>
    Task AddAsync(CalendarEvent ev, CancellationToken ct = default);

    /// <summary>
    /// Loads the event for mutation, applies <paramref name="mutate"/>, and
    /// saves. Returns <c>false</c> (without calling the mutator) when the
    /// event does not exist or is soft-deleted. The mutator is called against
    /// the tracked entity so changes are persisted on <c>SaveChanges</c>.
    /// </summary>
    Task<bool> UpdateAsync(
        Guid id,
        Action<CalendarEvent> mutate,
        CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes the event. Returns its <c>OwningTeamId</c> and previous
    /// <c>Title</c> so the caller can write an audit-log entry without
    /// re-loading, or <c>null</c> when the event does not exist or is already
    /// soft-deleted.
    /// </summary>
    Task<(Guid OwningTeamId, string Title)?> SoftDeleteAsync(
        Guid id,
        Instant deletedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Upserts the exception row for
    /// <c>(<paramref name="eventId"/>, <paramref name="originalOccurrenceStartUtc"/>)</c>.
    /// When no row exists, a new one is created using <paramref name="createdByUserId"/>
    /// and <paramref name="now"/> for audit stamps. When a row exists, only
    /// <c>UpdatedAt</c> is refreshed. The caller's <paramref name="apply"/>
    /// delegate mutates the exception (cancel flag and/or override fields)
    /// and is invoked after audit-stamp bookkeeping. Returns <c>true</c> on
    /// success. Validation is the caller's responsibility (via
    /// <see cref="CalendarEventException.Validate"/>).
    /// </summary>
    Task UpsertExceptionAsync(
        Guid eventId,
        Instant originalOccurrenceStartUtc,
        Guid createdByUserId,
        Instant now,
        Action<CalendarEventException> apply,
        CancellationToken ct = default);
}
