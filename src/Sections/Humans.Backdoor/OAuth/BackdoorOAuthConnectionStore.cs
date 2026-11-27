using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Humans.Backdoor.Data;
using Humans.Backdoor.Domain;
using NodaTime;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static Humans.Backdoor.OAuth.OAuthStoreValues;

namespace Humans.Backdoor.OAuth;

/// <summary>
/// OpenIddict's authorization store over Backdoor's own table. Queries run over every row in memory: the table holds a
/// handful of rows per person.
/// </summary>
internal sealed class BackdoorOAuthConnectionStore(IBackdoorOAuthRepository repository, IClock clock)
    : IOpenIddictAuthorizationStore<BackdoorOAuthConnection>
{
    public async ValueTask<long> CountAsync(CancellationToken cancellationToken) =>
        (await repository.GetConnectionsAsync(cancellationToken)).Count;

    public async ValueTask<long> CountAsync<TResult>(
        Func<IQueryable<BackdoorOAuthConnection>, IQueryable<TResult>> query, CancellationToken cancellationToken) =>
        query((await repository.GetConnectionsAsync(cancellationToken)).AsQueryable()).LongCount();

    public async ValueTask CreateAsync(BackdoorOAuthConnection authorization, CancellationToken cancellationToken)
    {
        if (authorization.Id == Guid.Empty) authorization.Id = Guid.NewGuid();
        if (authorization.CreatedAt == default) authorization.CreatedAt = clock.GetCurrentInstant();
        await repository.AddConnectionAsync(authorization, cancellationToken);
    }

    public async ValueTask DeleteAsync(BackdoorOAuthConnection authorization, CancellationToken cancellationToken) =>
        await repository.DeleteConnectionsAsync([authorization.Id], cancellationToken);

    public IAsyncEnumerable<BackdoorOAuthConnection> FindAsync(
        string? subject, string? client, string? status, string? type, ImmutableArray<string>? scopes,
        CancellationToken cancellationToken) =>
        WhereAsync(c =>
            (subject is null || Same(FormatId(c.UserId), subject))
            && (client is null || Same(c.ClientId, client))
            && (status is null || Same(c.Status, status))
            && (type is null || Same(c.Type, type))
            && (scopes is null || scopes.Value.All(s => ScopesOf(c).Contains(s, StringComparer.Ordinal))),
            cancellationToken);

    public IAsyncEnumerable<BackdoorOAuthConnection> FindByApplicationIdAsync(
        string identifier, CancellationToken cancellationToken) =>
        WhereAsync(c => Same(c.ClientId, identifier), cancellationToken);

    public async ValueTask<BackdoorOAuthConnection?> FindByIdAsync(string identifier, CancellationToken cancellationToken) =>
        ParseId(identifier) is { } id ? await repository.GetConnectionAsync(id, cancellationToken) : null;

    public IAsyncEnumerable<BackdoorOAuthConnection> FindBySubjectAsync(string subject, CancellationToken cancellationToken) =>
        WhereAsync(c => Same(FormatId(c.UserId), subject), cancellationToken);

    public ValueTask<string?> GetApplicationIdAsync(BackdoorOAuthConnection authorization, CancellationToken cancellationToken) =>
        new(authorization.ClientId);

    public async ValueTask<TResult?> GetAsync<TState, TResult>(
        Func<IQueryable<BackdoorOAuthConnection>, TState, IQueryable<TResult>> query, TState state,
        CancellationToken cancellationToken) =>
        query((await repository.GetConnectionsAsync(cancellationToken)).AsQueryable(), state).FirstOrDefault();

    public ValueTask<DateTimeOffset?> GetCreationDateAsync(
        BackdoorOAuthConnection authorization, CancellationToken cancellationToken) =>
        new(ToDateTimeOffset(authorization.CreatedAt));

    public ValueTask<string?> GetIdAsync(BackdoorOAuthConnection authorization, CancellationToken cancellationToken) =>
        new(FormatId(authorization.Id));

    public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(
        BackdoorOAuthConnection authorization, CancellationToken cancellationToken) =>
        new(ReadProperties(authorization.Properties));

    public ValueTask<ImmutableArray<string>> GetScopesAsync(
        BackdoorOAuthConnection authorization, CancellationToken cancellationToken) =>
        new([.. ScopesOf(authorization)]);

    public ValueTask<string?> GetStatusAsync(BackdoorOAuthConnection authorization, CancellationToken cancellationToken) =>
        new(authorization.Status);

    public ValueTask<string?> GetSubjectAsync(BackdoorOAuthConnection authorization, CancellationToken cancellationToken) =>
        new(FormatId(authorization.UserId));

    public ValueTask<string?> GetTypeAsync(BackdoorOAuthConnection authorization, CancellationToken cancellationToken) =>
        new(authorization.Type);

    public ValueTask<BackdoorOAuthConnection> InstantiateAsync(CancellationToken cancellationToken) =>
        new(new BackdoorOAuthConnection());

    public async IAsyncEnumerable<BackdoorOAuthConnection> ListAsync(
        int? count, int? offset, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var c in (await repository.GetConnectionsAsync(cancellationToken)).Skip(offset ?? 0).Take(count ?? int.MaxValue))
            yield return c;
    }

    public async IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
        Func<IQueryable<BackdoorOAuthConnection>, TState, IQueryable<TResult>> query, TState state,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var result in query((await repository.GetConnectionsAsync(cancellationToken)).AsQueryable(), state))
            yield return result;
    }

    /// <summary>Drops connections that no longer work and were made before <paramref name="threshold"/>.</summary>
    public async ValueTask<long> PruneAsync(DateTimeOffset threshold, CancellationToken cancellationToken)
    {
        var before = Instant.FromDateTimeOffset(threshold);
        var tokens = await repository.GetTokensAsync(cancellationToken);
        var stale = (await repository.GetConnectionsAsync(cancellationToken))
            .Where(c => c.CreatedAt < before
                && (!Same(c.Status, Statuses.Valid)
                    || (Same(c.Type, AuthorizationTypes.AdHoc) && !tokens.Any(t => t.ConnectionId == c.Id))))
            .Select(c => c.Id)
            .ToList();
        return stale.Count == 0 ? 0 : await repository.DeleteConnectionsAsync(stale, cancellationToken);
    }

    public async ValueTask<long> RevokeAsync(
        string? subject, string? client, string? status, string? type, CancellationToken cancellationToken) =>
        await RevokeWhereAsync(c =>
            (subject is null || Same(FormatId(c.UserId), subject))
            && (client is null || Same(c.ClientId, client))
            && (status is null || Same(c.Status, status))
            && (type is null || Same(c.Type, type)),
            cancellationToken);

    public async ValueTask<long> RevokeByApplicationIdAsync(string identifier, CancellationToken cancellationToken) =>
        await RevokeWhereAsync(c => Same(c.ClientId, identifier), cancellationToken);

    public async ValueTask<long> RevokeBySubjectAsync(string subject, CancellationToken cancellationToken) =>
        await RevokeWhereAsync(c => Same(FormatId(c.UserId), subject), cancellationToken);

    public ValueTask SetApplicationIdAsync(
        BackdoorOAuthConnection authorization, string? identifier, CancellationToken cancellationToken)
    {
        authorization.ClientId = identifier ?? string.Empty;
        return default;
    }

    public ValueTask SetCreationDateAsync(
        BackdoorOAuthConnection authorization, DateTimeOffset? date, CancellationToken cancellationToken)
    {
        if (ToInstant(date) is { } at) authorization.CreatedAt = at;
        return default;
    }

    public ValueTask SetPropertiesAsync(
        BackdoorOAuthConnection authorization, ImmutableDictionary<string, JsonElement> properties,
        CancellationToken cancellationToken)
    {
        authorization.Properties = WriteProperties(properties);
        return default;
    }

    public ValueTask SetScopesAsync(
        BackdoorOAuthConnection authorization, ImmutableArray<string> scopes, CancellationToken cancellationToken)
    {
        authorization.Scopes = string.Join(' ', scopes);
        return default;
    }

    public ValueTask SetStatusAsync(BackdoorOAuthConnection authorization, string? status, CancellationToken cancellationToken)
    {
        authorization.Status = status ?? string.Empty;
        return default;
    }

    /// <summary>Every subject is a person's id; anything else is a bug upstream.</summary>
    public ValueTask SetSubjectAsync(BackdoorOAuthConnection authorization, string? subject, CancellationToken cancellationToken)
    {
        authorization.UserId = ParseId(subject) ?? throw new InvalidOperationException("A connection's subject must be a user id.");
        return default;
    }

    public ValueTask SetTypeAsync(BackdoorOAuthConnection authorization, string? type, CancellationToken cancellationToken)
    {
        authorization.Type = type ?? string.Empty;
        return default;
    }

    public async ValueTask UpdateAsync(BackdoorOAuthConnection authorization, CancellationToken cancellationToken) =>
        await repository.UpdateConnectionAsync(authorization, cancellationToken);

    private static string[] ScopesOf(BackdoorOAuthConnection connection) =>
        connection.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private async IAsyncEnumerable<BackdoorOAuthConnection> WhereAsync(
        Func<BackdoorOAuthConnection, bool> predicate, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var c in (await repository.GetConnectionsAsync(cancellationToken)).Where(predicate))
            yield return c;
    }

    private async Task<long> RevokeWhereAsync(Func<BackdoorOAuthConnection, bool> predicate, CancellationToken cancellationToken)
    {
        var ids = (await repository.GetConnectionsAsync(cancellationToken)).Where(predicate).Select(c => c.Id).ToList();
        return ids.Count == 0 ? 0 : await repository.SetConnectionStatusAsync(ids, Statuses.Revoked, cancellationToken);
    }
}
