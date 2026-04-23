using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Calendar section's <c>calendar_events</c> and
/// <c>calendar_event_exceptions</c> tables. The only non-test file that
/// touches <c>DbContext.CalendarEvents</c> / <c>DbContext.CalendarEventExceptions</c>
/// after the Calendar §15 migration (issue #569) lands.
/// </summary>
/// <remarks>
/// <para>
/// Entities-in / entities-out per design-rules §3. Read methods are
/// <c>AsNoTracking</c>. Mutating methods load tracked entities and save
/// changes atomically inside a single
/// <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{TContext}"/>-owned
/// context so callers never have to reason about the EF context lifetime.
/// </para>
/// <para>
/// Cross-domain navigation (<c>CalendarEvent.OwningTeam</c>) is never
/// <c>Include</c>-ed; the application service stitches team display names
/// via <see cref="Teams.ITeamService"/> per design-rules §6.
/// </para>
/// </remarks>
public interface ICalendarRepository
{
    // ==========================================================================
    // Reads
    // ==========================================================================

    /// <summary>
    /// Returns calendar events whose window overlaps with
    /// <c>[from, to]</c> — i.e. events that start before <paramref name="to"/>
    /// and whose recurrence end (or <c>null</c>, meaning open-ended) is on or
    /// after <paramref name="from"/>. Optionally filtered by team.
    /// Exceptions are loaded eagerly. Soft-deleted rows are filtered via the
    /// global query filter. Read-only (<c>AsNoTracking</c>). The
    /// <c>OwningTeam</c> navigation is not loaded — the caller stitches team
    /// names via <see cref="Teams.ITeamService"/>.
    /// </summary>
    Task<IReadOnlyList<CalendarEvent>> GetEventsInWindowAsync(
        Instant from,
        Instant to,
        Guid? teamId,
        CancellationToken ct = default);

    /// <summary>
    /// Loads a single <see cref="CalendarEvent"/> by id, with its
    /// <c>Exceptions</c> collection included. Returns <c>null</c> if not
    /// found or soft-deleted. Read-only (<c>AsNoTracking</c>). The
    /// <c>OwningTeam</c> navigation is not loaded.
    /// </summary>
    Task<CalendarEvent?> GetEventByIdAsync(Guid id, CancellationToken ct = default);

    // ==========================================================================
    // Writes — CalendarEvent
    // ==========================================================================

    /// <summary>
    /// Inserts a new <see cref="CalendarEvent"/>. The caller is responsible for
    /// validating the entity first.
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
    /// Soft-deletes the event by stamping <c>DeletedAt</c> and
    /// <c>UpdatedAt</c> with <paramref name="deletedAt"/>. Returns the
    /// event's <c>OwningTeamId</c> and previous <c>Title</c> so the caller
    /// can write an audit-log entry without re-loading. Returns <c>null</c>
    /// when the event does not exist or is already soft-deleted.
    /// </summary>
    Task<(Guid OwningTeamId, string Title)?> SoftDeleteAsync(
        Guid id,
        Instant deletedAt,
        CancellationToken ct = default);

    // ==========================================================================
    // Writes — CalendarEventException (per-occurrence override / cancellation)
    // ==========================================================================

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
