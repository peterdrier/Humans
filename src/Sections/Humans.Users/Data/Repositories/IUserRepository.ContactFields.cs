using NodaTime;

namespace Humans.Users.Data.Repositories;

internal partial interface IUserRepository
{
    /// <summary>Every row, read-only, unordered.</summary>
    Task<IReadOnlyList<ContactField>> GetAllContactFieldsAsync(CancellationToken ct = default);

    /// <summary>Detached and unordered: mutated entities must go back to <see cref="BatchSaveAsync"/> as <c>toUpdate</c>.</summary>
    Task<IReadOnlyList<ContactField>> GetContactFieldsByUserIdAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>Adds, updates and removes in one save.</summary>
    Task BatchSaveAsync(
        IReadOnlyList<ContactField> toAdd,
        IReadOnlyList<ContactField> toUpdate,
        IReadOnlyList<ContactField> toRemove,
        CancellationToken ct = default);

    /// <summary>
    /// Account-merge fold: moves the source user's contact fields to the target. A source row
    /// matching a target row on <c>(FieldType, Value)</c> (case-insensitive) is dropped. No-op if
    /// the target has no profile.
    /// </summary>
    Task ReassignToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default);
}
