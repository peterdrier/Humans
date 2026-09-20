using Humans.Base.Interfaces.Repositories;
using Humans.Calendar.Domain;
using NodaTime;

namespace Humans.Calendar.Data;

/// <summary>
/// Repository for the Calendar section's <c>calendar_events</c>,
/// <c>calendar_event_exceptions</c> and <c>calendar_feed_tokens</c> tables. The only
/// non-test file that touches <c>DbContext.CalendarEvents</c> /
/// <c>DbContext.CalendarEventExceptions</c> / <c>DbContext.CalendarFeedTokens</c>.
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
    /// Upserts the exception row identifying the occurrence. An occurrence of an all-day
    /// series is named by <paramref name="originalDate"/>, one of a timed series by
    /// <paramref name="originalOccurrenceStartUtc"/>, and <c>calendar_event_exceptions</c>
    /// carries a unique index over each pairing with <paramref name="eventId"/>. Both
    /// arrive together in the one migration case: an all-day occurrence whose row predates
    /// the date columns, where the caller passes the date it is now named by plus the stale
    /// instant it was stored under, so the lookup still finds that row instead of inserting
    /// a duplicate. The write then leaves the row on date identity and clears the instant,
    /// so the migration happens once.
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
        Instant? originalOccurrenceStartUtc,
        Guid createdByUserId,
        Instant now,
        Action<CalendarEventException> apply,
        CancellationToken ct = default, LocalDate? originalDate = null);

    /// <summary>
    /// The member's personal iCal feed token, or <c>null</c> when they have none.
    /// Read-only (<c>AsNoTracking</c>).
    /// </summary>
    Task<Guid?> GetFeedTokenAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Gives the member <paramref name="candidate"/> if they have no token yet, and
    /// returns whichever token they end up with. Two first-time views racing each
    /// other both try to insert the same primary key; the loser reloads and gets the
    /// winner's token rather than a 500. Mint only — it never replaces a token that
    /// is already there, so it cannot revoke a live subscription by accident.
    /// </summary>
    Task<Guid> GetOrAddFeedTokenAsync(Guid userId, Guid candidate, CancellationToken ct = default);

    /// <summary>
    /// Upserts the member's feed token. Replacing an existing one revokes every
    /// URL handed out under it, so this is the deliberate rotation only: last write
    /// wins, which is what the member pressing the button expects.
    /// </summary>
    Task SetFeedTokenAsync(Guid userId, Guid token, CancellationToken ct = default);

    /// <summary>
    /// Drops the member's feed token row if there is one. Idempotent: the GDPR
    /// erasure cascade retries the whole chain after a mid-cascade failure.
    /// </summary>
    Task DeleteFeedTokenAsync(Guid userId, CancellationToken ct = default);
}
