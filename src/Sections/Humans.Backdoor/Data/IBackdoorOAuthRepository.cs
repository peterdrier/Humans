using Humans.Backdoor.Domain;
using Humans.Base.Interfaces.Repositories;

namespace Humans.Backdoor.Data;

/// <summary>
/// "Sign in with Humans" connections and their tokens. The lists load every row: both tables stay small, since expired
/// tokens are pruned daily.
/// </summary>
internal interface IBackdoorOAuthRepository : IRepository
{
    Task<BackdoorOAuthConnection?> GetConnectionAsync(Guid id, CancellationToken ct = default);

    /// <summary>Every connection, unordered, including revoked ones.</summary>
    Task<IReadOnlyList<BackdoorOAuthConnection>> GetConnectionsAsync(CancellationToken ct = default);

    Task AddConnectionAsync(BackdoorOAuthConnection connection, CancellationToken ct = default);

    Task UpdateConnectionAsync(BackdoorOAuthConnection connection, CancellationToken ct = default);

    /// <summary>Deletes the connections and, with them, their tokens. Returns how many connections went.</summary>
    Task<int> DeleteConnectionsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>Returns how many rows changed.</summary>
    Task<int> SetConnectionStatusAsync(IReadOnlyCollection<Guid> ids, string status, CancellationToken ct = default);

    Task<BackdoorOAuthToken?> GetTokenAsync(Guid id, CancellationToken ct = default);

    Task<BackdoorOAuthToken?> FindTokenByReferenceAsync(string referenceId, CancellationToken ct = default);

    /// <summary>Every token, unordered.</summary>
    Task<IReadOnlyList<BackdoorOAuthToken>> GetTokensAsync(CancellationToken ct = default);

    Task AddTokenAsync(BackdoorOAuthToken token, CancellationToken ct = default);

    Task UpdateTokenAsync(BackdoorOAuthToken token, CancellationToken ct = default);

    /// <summary>Returns how many rows went.</summary>
    Task<int> DeleteTokensAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>Returns how many rows changed.</summary>
    Task<int> SetTokenStatusAsync(IReadOnlyCollection<Guid> ids, string status, CancellationToken ct = default);

    /// <summary>Deletes the person's connections and tokens. Article 17: a credential has no basis to outlive its owner.</summary>
    Task EraseForUserAsync(Guid userId, CancellationToken ct = default);
}
