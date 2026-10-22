using Humans.Base.Interfaces.Repositories;
using Humans.Calendar.Domain;
using NodaTime;

namespace Humans.Calendar.Data;

internal interface ICalendarRepository : IRepository
{
    /// <summary>Includes exceptions. Null when missing or soft-deleted.</summary>
    Task<CalendarEvent?> GetEventByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Every non-soft-deleted event, with its exceptions.</summary>
    Task<IReadOnlyList<CalendarEvent>> GetAllAsync(CancellationToken ct = default);

    /// <summary>The caller validates the entity first.</summary>
    Task AddAsync(CalendarEvent ev, CancellationToken ct = default);

    /// <summary>Returns false, without calling <paramref name="mutate"/>, when the event is missing or soft-deleted.</summary>
    Task<bool> UpdateAsync(
        Guid id,
        Action<CalendarEvent> mutate,
        CancellationToken ct = default);

    /// <summary>Returns the owning team and old title for the audit entry, or null when missing or already deleted.</summary>
    Task<(Guid OwningTeamId, string Title)?> SoftDeleteAsync(
        Guid id,
        Instant deletedAt,
        CancellationToken ct = default);

    /// <summary>
    /// An all-day occurrence is identified by <paramref name="originalDate"/>, a timed one by
    /// <paramref name="originalOccurrenceStartUtc"/>. Both arrive only for an all-day row older than the date
    /// columns: the instant finds that row, which is then moved to date identity once, so no duplicate is inserted.
    /// <paramref name="apply"/> runs after the audit stamps. Returns null on success, or a validation reason without saving.
    /// </summary>
    Task<string?> UpsertExceptionAsync(
        Guid eventId,
        Instant? originalOccurrenceStartUtc,
        Guid createdByUserId,
        Instant now,
        Action<CalendarEventException> apply,
        CancellationToken ct = default, LocalDate? originalDate = null);

    /// <summary>Null when the member has none.</summary>
    Task<Guid?> GetFeedTokenAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Mint only: never replaces an existing token, so it can't revoke a live subscription. Two racing creates
    /// both insert the same key; the loser gets the winner's token instead of a 500.
    /// </summary>
    Task<(Guid Token, bool Created)> GetOrAddFeedTokenAsync(Guid userId, Guid candidate, CancellationToken ct = default);

    /// <summary>Rotation only: replacing the token revokes every URL handed out under it. Last write wins.</summary>
    Task SetFeedTokenAsync(Guid userId, Guid token, CancellationToken ct = default);

    /// <summary>Idempotent: the GDPR erasure cascade retries the whole chain after a failure.</summary>
    Task DeleteFeedTokenAsync(Guid userId, CancellationToken ct = default);
}
