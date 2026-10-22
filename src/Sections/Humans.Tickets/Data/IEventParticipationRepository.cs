using Humans.Base.Interfaces.Repositories;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain;
using NodaTime;

namespace Humans.Tickets.Data;

/// <summary>
/// Reads return cleared rows (null <see cref="EventParticipation.Status"/>) too: the service needs them to know
/// which legacy rows are shadowed. A <c>legacy</c> argument counts as the existing record when the table has none.
/// </summary>
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
        Instant? declaredAt, Instant? checkedInAt, EventParticipation? legacy, CancellationToken ct = default);

    /// <summary>
    /// Only when the source is <paramref name="requiredSource"/> and not Attended. With a <paramref name="legacy"/> row
    /// the record is cleared (null status) instead of deleted, so the legacy value stays shadowed.
    /// </summary>
    Task<bool> ClearAsync(
        Guid userId, int year, ParticipationSource requiredSource, EventParticipation? legacy,
        CancellationToken ct = default);

    /// <summary>Skips Attended records. Returns the entries that changed a record.</summary>
    Task<IReadOnlyList<(Guid UserId, ParticipationStatus Status)>> BackfillAsync(
        int year, IReadOnlyList<(Guid UserId, ParticipationStatus Status)> entries,
        IReadOnlyDictionary<Guid, EventParticipation> legacyByUser, CancellationToken ct = default);

    /// <summary>Skips a user and year that already has a row, cleared or not, so the legacy copy is idempotent.</summary>
    Task<bool> TryInsertAsync(EventParticipation row, CancellationToken ct = default);

    /// <summary>
    /// Per year the survivor keeps the higher status (Attended &gt; Ticketed &gt; NoShow &gt; NotAttending) and the
    /// first check-in. The source's record is removed, or cleared where it has a legacy row.
    /// </summary>
    Task ReassignAsync(
        Guid sourceUserId, Guid targetUserId,
        IReadOnlyList<EventParticipation> legacySource, IReadOnlyList<EventParticipation> legacyTarget,
        CancellationToken ct = default);
}
