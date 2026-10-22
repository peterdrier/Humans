using Humans.Base.Interfaces.Repositories;
using Humans.Email.Contracts;
using Humans.Email.Domain;

namespace Humans.Email.Data;

/// <summary>Reads include cleared rows (<c>OptedOut == null</c>); the service decides what they mean.</summary>
internal interface ICommunicationPreferenceRepository : IRepository
{
    Task<IReadOnlyList<CommunicationPreference>> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<CommunicationPreference>> GetAllAsync(CancellationToken ct = default);

    Task<IReadOnlyList<CommunicationPreference>> GetByCategoryAsync(MessageCategory category, CancellationToken ct = default);

    Task<IReadOnlyList<CommunicationPreference>> GetForUsersAsync(
        IReadOnlyList<Guid> userIds, MessageCategory category, CancellationToken ct = default);

    /// <summary>Inserts when the user and category have no row; an existing row keeps its id.</summary>
    Task UpsertAsync(CommunicationPreference row, CancellationToken ct = default);

    /// <summary>Returns false when there was no row.</summary>
    Task<bool> DeleteAsync(Guid userId, MessageCategory category, CancellationToken ct = default);

    /// <summary>Skips a user and category that already have a row. Returns whether it wrote.</summary>
    Task<bool> TryInsertAsync(CommunicationPreference row, CancellationToken ct = default);

    /// <summary>
    /// Makes each listed user's rows exactly the given set, in one save: updated in place by category, missing
    /// ones inserted, the rest deleted.
    /// </summary>
    Task ReplaceForUsersAsync(
        IReadOnlyList<(Guid UserId, IReadOnlyList<CommunicationPreference> Rows)> users, CancellationToken ct = default);

    Task<int> DeleteAllForUserAsync(Guid userId, CancellationToken ct = default);
}
