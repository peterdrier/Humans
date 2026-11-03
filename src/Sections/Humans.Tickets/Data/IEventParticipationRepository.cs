using Humans.Base.Interfaces.Repositories;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain;
using NodaTime;

namespace Humans.Tickets.Data;

internal interface IEventParticipationRepository : IRepository
{
    Task<IReadOnlyList<EventParticipation>> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<EventParticipation>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Attended is permanent (only a missing <c>CheckedInAt</c> may be filled); <c>CheckedInAt</c> is write-once.
    /// Returns false when refused: Attended with nothing to fill.
    /// </summary>
    Task<bool> UpsertAsync(
        Guid userId, int year, ParticipationStatus status, ParticipationSource source,
        Instant? declaredAt, Instant? checkedInAt, CancellationToken ct = default);

    /// <summary>
    /// Deletes only when the source is <paramref name="requiredSource"/> and not Attended.
    /// </summary>
    Task<bool> ClearAsync(
        Guid userId, int year, ParticipationSource requiredSource,
        CancellationToken ct = default);

    /// <summary>Skips Attended records. Returns the entries that changed a record.</summary>
    Task<IReadOnlyList<(Guid UserId, ParticipationStatus Status)>> BackfillAsync(
        int year, IReadOnlyList<(Guid UserId, ParticipationStatus Status)> entries,
        CancellationToken ct = default);

    /// <summary>
    /// Per year the survivor keeps the higher status (Attended &gt; Ticketed &gt; NoShow &gt; NotAttending) and the
    /// first check-in. The source's record is removed.
    /// </summary>
    Task ReassignAsync(
        Guid sourceUserId, Guid targetUserId,
        CancellationToken ct = default);
}
