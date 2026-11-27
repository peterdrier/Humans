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
/// OpenIddict's token store over Backdoor's own table. Queries run over every row in memory: expired tokens are pruned
/// daily, so the table stays small.
/// </summary>
internal sealed class BackdoorOAuthTokenStore(IBackdoorOAuthRepository repository, IClock clock)
    : IOpenIddictTokenStore<BackdoorOAuthToken>
{
    public async ValueTask<long> CountAsync(CancellationToken cancellationToken) =>
        (await repository.GetTokensAsync(cancellationToken)).Count;

    public async ValueTask<long> CountAsync<TResult>(
        Func<IQueryable<BackdoorOAuthToken>, IQueryable<TResult>> query, CancellationToken cancellationToken) =>
        query((await repository.GetTokensAsync(cancellationToken)).AsQueryable()).LongCount();

    public async ValueTask CreateAsync(BackdoorOAuthToken token, CancellationToken cancellationToken)
    {
        if (token.Id == Guid.Empty) token.Id = Guid.NewGuid();
        await repository.AddTokenAsync(token, cancellationToken);
    }

    public async ValueTask DeleteAsync(BackdoorOAuthToken token, CancellationToken cancellationToken) =>
        await repository.DeleteTokensAsync([token.Id], cancellationToken);

    public IAsyncEnumerable<BackdoorOAuthToken> FindAsync(
        string? subject, string? client, string? status, string? type, CancellationToken cancellationToken) =>
        WhereAsync(t =>
            (subject is null || Same(FormatId(t.UserId), subject))
            && (client is null || Same(t.ClientId, client))
            && (status is null || Same(t.Status, status))
            && (type is null || Same(t.Type, type)),
            cancellationToken);

    public IAsyncEnumerable<BackdoorOAuthToken> FindByApplicationIdAsync(string identifier, CancellationToken cancellationToken) =>
        WhereAsync(t => Same(t.ClientId, identifier), cancellationToken);

    public IAsyncEnumerable<BackdoorOAuthToken> FindByAuthorizationIdAsync(string identifier, CancellationToken cancellationToken) =>
        WhereAsync(t => Same(FormatId(t.ConnectionId), identifier), cancellationToken);

    public async ValueTask<BackdoorOAuthToken?> FindByIdAsync(string identifier, CancellationToken cancellationToken) =>
        ParseId(identifier) is { } id ? await repository.GetTokenAsync(id, cancellationToken) : null;

    public async ValueTask<BackdoorOAuthToken?> FindByReferenceIdAsync(string identifier, CancellationToken cancellationToken) =>
        await repository.FindTokenByReferenceAsync(identifier, cancellationToken);

    public IAsyncEnumerable<BackdoorOAuthToken> FindBySubjectAsync(string subject, CancellationToken cancellationToken) =>
        WhereAsync(t => Same(FormatId(t.UserId), subject), cancellationToken);

    public ValueTask<string?> GetApplicationIdAsync(BackdoorOAuthToken token, CancellationToken cancellationToken) =>
        new(token.ClientId);

    public async ValueTask<TResult?> GetAsync<TState, TResult>(
        Func<IQueryable<BackdoorOAuthToken>, TState, IQueryable<TResult>> query, TState state,
        CancellationToken cancellationToken) =>
        query((await repository.GetTokensAsync(cancellationToken)).AsQueryable(), state).FirstOrDefault();

    public ValueTask<string?> GetAuthorizationIdAsync(BackdoorOAuthToken token, CancellationToken cancellationToken) =>
        new(FormatId(token.ConnectionId));

    public ValueTask<DateTimeOffset?> GetCreationDateAsync(BackdoorOAuthToken token, CancellationToken cancellationToken) =>
        new(ToDateTimeOffset(token.CreatedAt));

    public ValueTask<DateTimeOffset?> GetExpirationDateAsync(BackdoorOAuthToken token, CancellationToken cancellationToken) =>
        new(ToDateTimeOffset(token.ExpiresAt));

    public ValueTask<string?> GetIdAsync(BackdoorOAuthToken token, CancellationToken cancellationToken) =>
        new(FormatId(token.Id));

    public ValueTask<string?> GetPayloadAsync(BackdoorOAuthToken token, CancellationToken cancellationToken) =>
        new(token.Payload);

    public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(
        BackdoorOAuthToken token, CancellationToken cancellationToken) =>
        new(ReadProperties(token.Properties));

    public ValueTask<DateTimeOffset?> GetRedemptionDateAsync(BackdoorOAuthToken token, CancellationToken cancellationToken) =>
        new(ToDateTimeOffset(token.RedeemedAt));

    public ValueTask<string?> GetReferenceIdAsync(BackdoorOAuthToken token, CancellationToken cancellationToken) =>
        new(token.ReferenceId);

    public ValueTask<string?> GetStatusAsync(BackdoorOAuthToken token, CancellationToken cancellationToken) =>
        new(token.Status);

    public ValueTask<string?> GetSubjectAsync(BackdoorOAuthToken token, CancellationToken cancellationToken) =>
        new(FormatId(token.UserId));

    public ValueTask<string?> GetTypeAsync(BackdoorOAuthToken token, CancellationToken cancellationToken) =>
        new(token.Type);

    public ValueTask<BackdoorOAuthToken> InstantiateAsync(CancellationToken cancellationToken) =>
        new(new BackdoorOAuthToken());

    public async IAsyncEnumerable<BackdoorOAuthToken> ListAsync(
        int? count, int? offset, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var t in (await repository.GetTokensAsync(cancellationToken)).Skip(offset ?? 0).Take(count ?? int.MaxValue))
            yield return t;
    }

    public async IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
        Func<IQueryable<BackdoorOAuthToken>, TState, IQueryable<TResult>> query, TState state,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var result in query((await repository.GetTokensAsync(cancellationToken)).AsQueryable(), state))
            yield return result;
    }

    /// <summary>
    /// Drops tokens made before <paramref name="threshold"/> that can no longer be used: expired, spent, revoked, or
    /// under a connection that no longer works.
    /// </summary>
    public async ValueTask<long> PruneAsync(DateTimeOffset threshold, CancellationToken cancellationToken)
    {
        var before = Instant.FromDateTimeOffset(threshold);
        var now = clock.GetCurrentInstant();
        var working = (await repository.GetConnectionsAsync(cancellationToken))
            .Where(c => Same(c.Status, Statuses.Valid))
            .Select(c => c.Id)
            .ToHashSet();
        var stale = (await repository.GetTokensAsync(cancellationToken))
            .Where(t => t.CreatedAt < before
                && ((!Same(t.Status, Statuses.Inactive) && !Same(t.Status, Statuses.Valid))
                    || (t.ConnectionId is { } c && !working.Contains(c))
                    || t.ExpiresAt < now))
            .Select(t => t.Id)
            .ToList();
        return stale.Count == 0 ? 0 : await repository.DeleteTokensAsync(stale, cancellationToken);
    }

    public async ValueTask<long> RevokeAsync(
        string? subject, string? client, string? status, string? type, CancellationToken cancellationToken) =>
        await RevokeWhereAsync(t =>
            (subject is null || Same(FormatId(t.UserId), subject))
            && (client is null || Same(t.ClientId, client))
            && (status is null || Same(t.Status, status))
            && (type is null || Same(t.Type, type)),
            cancellationToken);

    public async ValueTask<long> RevokeByApplicationIdAsync(string identifier, CancellationToken cancellationToken = default) =>
        await RevokeWhereAsync(t => Same(t.ClientId, identifier), cancellationToken);

    public async ValueTask<long> RevokeByAuthorizationIdAsync(string identifier, CancellationToken cancellationToken) =>
        await RevokeWhereAsync(t => Same(FormatId(t.ConnectionId), identifier), cancellationToken);

    public async ValueTask<long> RevokeBySubjectAsync(string subject, CancellationToken cancellationToken = default) =>
        await RevokeWhereAsync(t => Same(FormatId(t.UserId), subject), cancellationToken);

    public ValueTask SetApplicationIdAsync(BackdoorOAuthToken token, string? identifier, CancellationToken cancellationToken)
    {
        token.ClientId = identifier;
        return default;
    }

    public ValueTask SetAuthorizationIdAsync(BackdoorOAuthToken token, string? identifier, CancellationToken cancellationToken)
    {
        token.ConnectionId = ParseId(identifier);
        return default;
    }

    public ValueTask SetCreationDateAsync(BackdoorOAuthToken token, DateTimeOffset? date, CancellationToken cancellationToken)
    {
        token.CreatedAt = ToInstant(date);
        return default;
    }

    public ValueTask SetExpirationDateAsync(BackdoorOAuthToken token, DateTimeOffset? date, CancellationToken cancellationToken)
    {
        token.ExpiresAt = ToInstant(date);
        return default;
    }

    public ValueTask SetPayloadAsync(BackdoorOAuthToken token, string? payload, CancellationToken cancellationToken)
    {
        token.Payload = payload;
        return default;
    }

    public ValueTask SetPropertiesAsync(
        BackdoorOAuthToken token, ImmutableDictionary<string, JsonElement> properties, CancellationToken cancellationToken)
    {
        token.Properties = WriteProperties(properties);
        return default;
    }

    public ValueTask SetRedemptionDateAsync(BackdoorOAuthToken token, DateTimeOffset? date, CancellationToken cancellationToken)
    {
        token.RedeemedAt = ToInstant(date);
        return default;
    }

    public ValueTask SetReferenceIdAsync(BackdoorOAuthToken token, string? identifier, CancellationToken cancellationToken)
    {
        token.ReferenceId = identifier;
        return default;
    }

    public ValueTask SetStatusAsync(BackdoorOAuthToken token, string? status, CancellationToken cancellationToken)
    {
        token.Status = status ?? string.Empty;
        return default;
    }

    public ValueTask SetSubjectAsync(BackdoorOAuthToken token, string? subject, CancellationToken cancellationToken)
    {
        token.UserId = ParseId(subject);
        return default;
    }

    public ValueTask SetTypeAsync(BackdoorOAuthToken token, string? type, CancellationToken cancellationToken)
    {
        token.Type = type ?? string.Empty;
        return default;
    }

    public async ValueTask UpdateAsync(BackdoorOAuthToken token, CancellationToken cancellationToken) =>
        await repository.UpdateTokenAsync(token, cancellationToken);

    private async IAsyncEnumerable<BackdoorOAuthToken> WhereAsync(
        Func<BackdoorOAuthToken, bool> predicate, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var t in (await repository.GetTokensAsync(cancellationToken)).Where(predicate))
            yield return t;
    }

    private async Task<long> RevokeWhereAsync(Func<BackdoorOAuthToken, bool> predicate, CancellationToken cancellationToken)
    {
        var ids = (await repository.GetTokensAsync(cancellationToken)).Where(predicate).Select(t => t.Id).ToList();
        return ids.Count == 0 ? 0 : await repository.SetTokenStatusAsync(ids, Statuses.Revoked, cancellationToken);
    }
}
