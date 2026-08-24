using Humans.Backdoor.Domain;
using Humans.Base.Interfaces.Repositories;
using NodaTime;

namespace Humans.Backdoor.Data;

/// <summary>
/// Repository for the Backdoor section's one table, <c>backdoor_api_keys</c>. The only
/// non-test file that reads or writes it.
/// </summary>
internal interface IBackdoorApiKeyRepository : IRepository
{
    /// <summary>
    /// The active (unrevoked) row whose stored hash equals <paramref name="keyHash"/>, or
    /// null. The presented plaintext is hashed by the caller; this never sees it.
    /// </summary>
    Task<BackdoorApiKey?> FindActiveByHashAsync(string keyHash, CancellationToken ct = default);

    /// <summary>Every row, unordered — the admin list, including revoked history.</summary>
    Task<IReadOnlyList<BackdoorApiKey>> GetAllAsync(CancellationToken ct = default);

    Task<BackdoorApiKey?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task AddAsync(BackdoorApiKey key, CancellationToken ct = default);

    /// <summary>
    /// Stamps <c>RevokedAt</c>/<c>RevokedByUserId</c> on an active row. Returns false if the
    /// row is missing or already revoked.
    /// </summary>
    Task<bool> RevokeAsync(Guid id, Guid revokedByUserId, Instant at, CancellationToken ct = default);

    /// <summary>Stamps <c>LastUsedAt</c>. Fire-and-forget from the auth filter.</summary>
    Task TouchAsync(Guid id, Instant at, CancellationToken ct = default);

    /// <summary>Every row belonging to one human, unordered — the GDPR export slice.</summary>
    Task<IReadOnlyList<BackdoorApiKey>> GetForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Hard-deletes every key owned by <paramref name="userId"/> and detaches them as the
    /// revoker of anyone else's key. Article 17 — a credential has no basis to outlive its owner.
    /// </summary>
    Task EraseForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Re-points owner and actor columns from the eliminated user onto the survivor.</summary>
    Task ReassignToUserAsync(Guid fromUserId, Guid toUserId, CancellationToken ct = default);
}
