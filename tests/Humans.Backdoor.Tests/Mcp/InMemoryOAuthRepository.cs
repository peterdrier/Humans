using System.Collections.Concurrent;
using Humans.Backdoor.Data;
using Humans.Backdoor.Domain;

namespace Humans.Backdoor.Tests.Mcp;

/// <summary>The OAuth tables as lists, so the sign-in flow runs end to end without a database.</summary>
internal sealed class InMemoryOAuthRepository : IBackdoorOAuthRepository
{
    private readonly ConcurrentDictionary<Guid, BackdoorOAuthConnection> _connections = new();
    private readonly ConcurrentDictionary<Guid, BackdoorOAuthToken> _tokens = new();

    public Task<BackdoorOAuthConnection?> GetConnectionAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_connections.TryGetValue(id, out var c) ? Copy(c) : null);

    public Task<IReadOnlyList<BackdoorOAuthConnection>> GetConnectionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BackdoorOAuthConnection>>([.. _connections.Values.Select(Copy)]);

    public Task AddConnectionAsync(BackdoorOAuthConnection connection, CancellationToken ct = default) =>
        UpdateConnectionAsync(connection, ct);

    public Task UpdateConnectionAsync(BackdoorOAuthConnection connection, CancellationToken ct = default)
    {
        _connections[connection.Id] = Copy(connection);
        return Task.CompletedTask;
    }

    public Task<int> DeleteConnectionsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        foreach (var token in _tokens.Values.Where(t => t.ConnectionId is { } c && ids.Contains(c)))
            _tokens.TryRemove(token.Id, out _);
        return Task.FromResult(ids.Count(id => _connections.TryRemove(id, out _)));
    }

    public Task<int> SetConnectionStatusAsync(IReadOnlyCollection<Guid> ids, string status, CancellationToken ct = default)
    {
        var changed = 0;
        foreach (var id in ids)
        {
            if (!_connections.TryGetValue(id, out var c)) continue;
            c.Status = status;
            changed++;
        }

        return Task.FromResult(changed);
    }

    public Task<BackdoorOAuthToken?> GetTokenAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_tokens.TryGetValue(id, out var t) ? Copy(t) : null);

    public Task<BackdoorOAuthToken?> FindTokenByReferenceAsync(string referenceId, CancellationToken ct = default) =>
        Task.FromResult(_tokens.Values.Where(t => string.Equals(t.ReferenceId, referenceId, StringComparison.Ordinal)).Select(Copy).FirstOrDefault());

    public Task<IReadOnlyList<BackdoorOAuthToken>> GetTokensAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BackdoorOAuthToken>>([.. _tokens.Values.Select(Copy)]);

    public Task AddTokenAsync(BackdoorOAuthToken token, CancellationToken ct = default) => UpdateTokenAsync(token, ct);

    public Task UpdateTokenAsync(BackdoorOAuthToken token, CancellationToken ct = default)
    {
        _tokens[token.Id] = Copy(token);
        return Task.CompletedTask;
    }

    public Task<int> DeleteTokensAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        Task.FromResult(ids.Count(id => _tokens.TryRemove(id, out _)));

    public Task<int> SetTokenStatusAsync(IReadOnlyCollection<Guid> ids, string status, CancellationToken ct = default)
    {
        var changed = 0;
        foreach (var id in ids)
        {
            if (!_tokens.TryGetValue(id, out var t)) continue;
            t.Status = status;
            changed++;
        }

        return Task.FromResult(changed);
    }

    public Task EraseForUserAsync(Guid userId, CancellationToken ct = default)
    {
        foreach (var t in _tokens.Values.Where(t => t.UserId == userId)) _tokens.TryRemove(t.Id, out _);
        foreach (var c in _connections.Values.Where(c => c.UserId == userId)) _connections.TryRemove(c.Id, out _);
        return Task.CompletedTask;
    }

    // Copies stand in for the database's detached rows: a caller's later edits never leak into the store.
    private static BackdoorOAuthConnection Copy(BackdoorOAuthConnection c) => new()
    {
        Id = c.Id, UserId = c.UserId, ClientId = c.ClientId, Status = c.Status, Type = c.Type, Scopes = c.Scopes,
        CreatedAt = c.CreatedAt, Properties = c.Properties,
    };

    private static BackdoorOAuthToken Copy(BackdoorOAuthToken t) => new()
    {
        Id = t.Id, ConnectionId = t.ConnectionId, ClientId = t.ClientId, UserId = t.UserId, Type = t.Type,
        Status = t.Status, CreatedAt = t.CreatedAt, ExpiresAt = t.ExpiresAt, RedeemedAt = t.RedeemedAt,
        ReferenceId = t.ReferenceId, Payload = t.Payload, Properties = t.Properties,
    };
}
