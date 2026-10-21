using Humans.Base.Interfaces.Repositories;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain;
using NodaTime;

namespace Humans.Tickets.Data;

/// <summary>
/// The only reader and writer of <c>tickets_event_participations</c>. Reads return cleared rows
/// (<see cref="EventParticipation.Status"/> null, written before the legacy Users rows were
/// retired) too; the service skips them.
/// </summary>
internal interface IEventParticipationRepository : IRepository
{
    Task<IReadOnlyList<EventParticipation>> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<EventParticipation>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Upsert one record. On an existing record: Attended is permanent (only a missing
    /// <c>CheckedInAt</c> may be filled by a later Attended write); anything else takes the new
    /// status, source and declared-at. <c>CheckedInAt</c> is write-once. Returns false when the write
    /// was refused because the record is Attended and there was nothing to fill.
    /// </summary>
    Task<bool> UpsertAsync(
        Guid userId, int year, ParticipationStatus status, ParticipationSource source,
        Instant? declaredAt, Instant? checkedInAt, CancellationToken ct = default);

    /// <summary>
    /// Delete the record when its source is <paramref name="requiredSource"/> and it is not
    /// Attended. Returns true when a record was removed.
    /// </summary>
    Task<bool> ClearAsync(
        Guid userId, int year, ParticipationSource requiredSource, CancellationToken ct = default);

    /// <summary>
    /// Admin backfill for one year: each entry takes its status with source AdminBackfill unless
    /// the record is Attended. Returns the entries that changed a record.
    /// </summary>
    Task<IReadOnlyList<(Guid UserId, ParticipationStatus Status)>> BackfillAsync(
        int year, IReadOnlyList<(Guid UserId, ParticipationStatus Status)> entries,
        CancellationToken ct = default);

    /// <summary>
    /// Account-merge fold of <paramref name="sourceUserId"/> onto <paramref name="targetUserId"/>,
    /// per year: the survivor keeps the higher-precedence status (Attended &gt; Ticketed &gt; NoShow
    /// &gt; NotAttending) and the first check-in. The source's records are then removed.
    /// </summary>
    Task ReassignAsync(Guid sourceUserId, Guid targetUserId, CancellationToken ct = default);
}
