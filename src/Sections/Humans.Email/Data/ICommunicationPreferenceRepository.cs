using Humans.Base.Interfaces.Repositories;
using Humans.Email.Contracts;
using Humans.Email.Domain;

namespace Humans.Email.Data;

/// <summary>
/// Repository for <c>email_communication_preferences</c>. The only non-test file that touches
/// <c>EmailDbContext.CommunicationPreferences</c>. Reads return detached rows, cleared
/// (<c>OptedOut == null</c>) rows included; the service decides what they mean.
/// </summary>
internal interface ICommunicationPreferenceRepository : IRepository
{
    Task<IReadOnlyList<CommunicationPreference>> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<CommunicationPreference>> GetAllAsync(CancellationToken ct = default);

    Task<IReadOnlyList<CommunicationPreference>> GetByCategoryAsync(MessageCategory category, CancellationToken ct = default);

    Task<IReadOnlyList<CommunicationPreference>> GetForUsersAsync(
        IReadOnlyList<Guid> userIds, MessageCategory category, CancellationToken ct = default);

    /// <summary>
    /// Writes <paramref name="row"/>'s state onto the user+category row, inserting it when none exists.
    /// The id of an existing row is kept.
    /// </summary>
    Task UpsertAsync(CommunicationPreference row, CancellationToken ct = default);

    /// <summary>Deletes the user+category row. Returns false when there was none.</summary>
    Task<bool> DeleteAsync(Guid userId, MessageCategory category, CancellationToken ct = default);

    /// <summary>Inserts <paramref name="row"/> unless the user+category already has one. Returns whether it wrote.</summary>
    Task<bool> TryInsertAsync(CommunicationPreference row, CancellationToken ct = default);

    /// <summary>
    /// Makes each listed user's rows exactly the given set, in one save: rows are updated in place by
    /// category, missing ones inserted, the rest deleted. Used by the account-merge fold.
    /// </summary>
    Task ReplaceForUsersAsync(
        IReadOnlyList<(Guid UserId, IReadOnlyList<CommunicationPreference> Rows)> users, CancellationToken ct = default);

    /// <summary>GDPR Art. 17: removes every row for the user.</summary>
    Task<int> DeleteAllForUserAsync(Guid userId, CancellationToken ct = default);
}
