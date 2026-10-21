using Humans.Users.Contracts;
using Humans.Base.Interfaces.Repositories;

namespace Humans.Users.Data.Repositories;

/// <summary>
/// Repository for the frozen legacy <c>communication_preferences</c> table (Email owns the live
/// preferences since Users decomposition commit 14). Reads feed the <see cref="UserInfo"/> cache;
/// the one write left is GDPR erasure. The only non-test file that touches this DbSet.
/// </summary>
internal interface ICommunicationPreferenceRepository : IRepository
{
    /// <summary>
    /// Returns all legacy preferences for a user, read-only. Used by the
    /// <see cref="UserInfo"/> cache rebuild path and GDPR export.
    /// </summary>
    Task<IReadOnlyList<CommunicationPreference>> GetByUserIdReadOnlyAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns every <c>communication_preferences</c> row, read-only. Used by
    /// the <see cref="UserInfo"/> cache warm path to bulk-load preferences
    /// once at startup.
    /// </summary>
    Task<IReadOnlyList<CommunicationPreference>> GetAllAsync(CancellationToken ct = default);

    /// <summary>GDPR Art. 17: removes every communication preference row for the user.</summary>
    Task<int> DeleteAllForUserAsync(Guid userId, CancellationToken ct = default);
}
