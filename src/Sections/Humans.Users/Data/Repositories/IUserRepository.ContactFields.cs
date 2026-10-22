using NodaTime;

namespace Humans.Users.Data.Repositories;

internal partial interface IUserRepository
{
    /// <summary>Read-only, ordered by <c>DisplayOrder</c> then <c>CreatedAt</c>.</summary>
    Task<IReadOnlyList<ContactField>> GetByProfileIdReadOnlyAsync(
        Guid profileId, CancellationToken ct = default);

    /// <summary>Every row, read-only, unordered.</summary>
    Task<IReadOnlyList<ContactField>> GetAllContactFieldsAsync(CancellationToken ct = default);

    /// <summary>Detached: mutated entities must go back to <see cref="BatchSaveAsync"/> as <c>toUpdate</c>.</summary>
    Task<IReadOnlyList<ContactField>> GetByProfileIdForMutationAsync(
        Guid profileId, CancellationToken ct = default);

    /// <summary>Adds, updates and removes in one save.</summary>
    Task BatchSaveAsync(
        IReadOnlyList<ContactField> toAdd,
        IReadOnlyList<ContactField> toUpdate,
        IReadOnlyList<ContactField> toRemove,
        CancellationToken ct = default);

    /// <summary>
    /// Account-merge fold: re-FKs the source profile's contact fields to the target's. A source row
    /// matching a target row on <c>(FieldType, Value)</c> (case-insensitive) is dropped. No-op if
    /// either user has no profile.
    /// </summary>
    Task ReassignToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default);
}
