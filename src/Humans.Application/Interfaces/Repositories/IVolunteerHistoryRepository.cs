using Humans.Domain.Entities;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the <c>volunteer_history_entries</c> table.
/// The only non-test file that may write to this DbSet.
/// </summary>
public interface IVolunteerHistoryRepository
{
    /// <summary>
    /// Returns all entries for a profile, read-only, ordered by
    /// <c>Date</c> descending then <c>CreatedAt</c> descending.
    /// </summary>
    Task<IReadOnlyList<VolunteerHistoryEntry>> GetByProfileIdReadOnlyAsync(
        Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Returns all entries for a profile as tracked entities for
    /// in-place modification. Used by the batch save flow.
    /// </summary>
    Task<IReadOnlyList<VolunteerHistoryEntry>> GetByProfileIdTrackedAsync(
        Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Atomic batch write: adds new entries, removes deleted entries, and
    /// persists all changes (including mutations on tracked entities loaded
    /// via <see cref="GetByProfileIdTrackedAsync"/>) in one
    /// <c>SaveChangesAsync</c> call.
    /// </summary>
    Task BatchSaveAsync(
        IReadOnlyList<VolunteerHistoryEntry> toAdd,
        IReadOnlyList<VolunteerHistoryEntry> toRemove,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes all volunteer history entries owned by the given profile.
    /// Used by GDPR right-to-deletion flows.
    /// </summary>
    Task DeleteAllForProfileAsync(Guid profileId, CancellationToken ct = default);
}
