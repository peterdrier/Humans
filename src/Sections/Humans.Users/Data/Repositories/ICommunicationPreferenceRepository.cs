using Humans.Users.Contracts;
using Humans.Base.Interfaces.Repositories;

namespace Humans.Users.Data.Repositories;

/// <summary>
/// The frozen legacy <c>communication_preferences</c> table (Email owns the live preferences).
/// Reads feed the <see cref="UserInfo"/> cache; the one write left is GDPR erasure.
/// </summary>
internal interface ICommunicationPreferenceRepository : IRepository
{
    Task<IReadOnlyList<CommunicationPreference>> GetByUserIdReadOnlyAsync(
        Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<CommunicationPreference>> GetAllAsync(CancellationToken ct = default);

    /// <summary>GDPR Art. 17: removes every communication preference row for the user.</summary>
    Task<int> DeleteAllForUserAsync(Guid userId, CancellationToken ct = default);
}
