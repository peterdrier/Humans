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
    /// Returns detached entities intended to be mutated in-memory and passed back
    /// to <see cref="BatchSaveAsync"/> in the <c>toUpdate</c> list. The returned
    /// entities are NOT tracked — callers must explicitly hand mutated entities
    /// back to the batch save flow for persistence.
    /// </summary>
    Task<IReadOnlyList<VolunteerHistoryEntry>> GetByProfileIdForMutationAsync(
        Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Atomic batch write: adds new entries, updates mutated entries, removes
    /// deleted entries, and persists all changes in one <c>SaveChangesAsync</c> call.
    /// Callers that previously relied on EF change-tracking for in-place mutations
    /// should pass the mutated entities in <paramref name="toUpdate"/>.
    /// </summary>
    Task BatchSaveAsync(
        IReadOnlyList<VolunteerHistoryEntry> toAdd,
        IReadOnlyList<VolunteerHistoryEntry> toUpdate,
        IReadOnlyList<VolunteerHistoryEntry> toRemove,
        CancellationToken ct = default);
}
