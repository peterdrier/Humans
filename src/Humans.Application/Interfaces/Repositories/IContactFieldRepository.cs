using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the <c>contact_fields</c> table.
/// The only non-test file that may write to this DbSet.
/// </summary>
public interface IContactFieldRepository
{
    /// <summary>
    /// Returns all contact fields for a profile, read-only, ordered by
    /// <c>DisplayOrder</c> then <c>CreatedAt</c>.
    /// </summary>
    Task<IReadOnlyList<ContactField>> GetByProfileIdReadOnlyAsync(
        Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Returns contact fields for a profile filtered by allowed visibility
    /// levels, read-only, ordered by <c>DisplayOrder</c> then <c>CreatedAt</c>.
    /// </summary>
    Task<IReadOnlyList<ContactField>> GetVisibleByProfileIdAsync(
        Guid profileId, IReadOnlyList<ContactFieldVisibility> allowedVisibilities,
        CancellationToken ct = default);

    /// <summary>
    /// Returns detached entities intended to be mutated in-memory and passed back
    /// to <see cref="BatchSaveAsync"/> in the <c>toUpdate</c> list. The returned
    /// entities are NOT tracked — callers must explicitly hand mutated entities
    /// back to the batch save flow for persistence.
    /// </summary>
    Task<IReadOnlyList<ContactField>> GetByProfileIdForMutationAsync(
        Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// Atomic batch write: adds new fields, updates mutated fields, removes
    /// deleted fields, and persists all changes in one <c>SaveChangesAsync</c> call.
    /// Callers that previously relied on EF change-tracking for in-place mutations
    /// should pass the mutated entities in <paramref name="toUpdate"/>.
    /// </summary>
    Task BatchSaveAsync(
        IReadOnlyList<ContactField> toAdd,
        IReadOnlyList<ContactField> toUpdate,
        IReadOnlyList<ContactField> toRemove,
        CancellationToken ct = default);

    /// <summary>
    /// Bulk-moves <c>contact_fields</c> rows from <paramref name="sourceUserId"/>'s
    /// profile to <paramref name="targetUserId"/>'s profile for the
    /// account-merge fold flow. Conflict rule per the fold spec: when source
    /// and target both have a row with the same <c>(FieldType, Value)</c>
    /// (case-insensitive on <c>Value</c>), the source row is dropped — target's
    /// row wins on collision. Surviving source rows are re-FK'd to the target's
    /// profile. <c>UpdatedAt</c> is stamped to <paramref name="updatedAt"/> on
    /// every row touched. Returns the count of <c>contact_fields</c> rows
    /// ultimately attributed to <paramref name="targetUserId"/>'s profile.
    /// Returns 0 if either user has no profile.
    /// </summary>
    Task<int> ReassignToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default);
}
