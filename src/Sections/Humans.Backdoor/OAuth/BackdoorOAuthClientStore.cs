using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Humans.Backdoor.OAuth;

/// <summary>OpenIddict's application store over <see cref="BackdoorOAuthClients"/>: read only, since clients live in code.</summary>
internal sealed class BackdoorOAuthClientStore : IOpenIddictApplicationStore<BackdoorOAuthClient>
{
    private const string ReadOnly = "Clients are registered in code.";

    public ValueTask<long> CountAsync(CancellationToken cancellationToken) => new(BackdoorOAuthClients.All.Count);

    public ValueTask<long> CountAsync<TResult>(
        Func<IQueryable<BackdoorOAuthClient>, IQueryable<TResult>> query, CancellationToken cancellationToken) =>
        new(query(BackdoorOAuthClients.All.AsQueryable()).LongCount());

    public ValueTask CreateAsync(BackdoorOAuthClient application, CancellationToken cancellationToken) =>
        throw new NotSupportedException(ReadOnly);

    public ValueTask DeleteAsync(BackdoorOAuthClient application, CancellationToken cancellationToken) =>
        throw new NotSupportedException(ReadOnly);

    public ValueTask<BackdoorOAuthClient?> FindByIdAsync(string identifier, CancellationToken cancellationToken) =>
        new(BackdoorOAuthClients.Find(identifier));

    public ValueTask<BackdoorOAuthClient?> FindByClientIdAsync(string identifier, CancellationToken cancellationToken) =>
        new(BackdoorOAuthClients.Find(identifier));

    public async IAsyncEnumerable<BackdoorOAuthClient> FindByPostLogoutRedirectUriAsync(
        string uri, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    public async IAsyncEnumerable<BackdoorOAuthClient> FindByRedirectUriAsync(
        string uri, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        foreach (var client in BackdoorOAuthClients.All.Where(c => string.Equals(c.RedirectUri, uri, StringComparison.Ordinal)))
            yield return client;
    }

    public ValueTask<string?> GetApplicationTypeAsync(BackdoorOAuthClient application, CancellationToken cancellationToken) =>
        new(ApplicationTypes.Web);

    public ValueTask<TResult?> GetAsync<TState, TResult>(
        Func<IQueryable<BackdoorOAuthClient>, TState, IQueryable<TResult>> query, TState state,
        CancellationToken cancellationToken) =>
        new(query(BackdoorOAuthClients.All.AsQueryable(), state).FirstOrDefault());

    public ValueTask<string?> GetClientIdAsync(BackdoorOAuthClient application, CancellationToken cancellationToken) =>
        new(application.ClientId);

    public ValueTask<string?> GetClientSecretAsync(BackdoorOAuthClient application, CancellationToken cancellationToken) =>
        new((string?)null);

    public ValueTask<string?> GetClientTypeAsync(BackdoorOAuthClient application, CancellationToken cancellationToken) =>
        new(ClientTypes.Public);

    /// <summary>Explicit: the person is asked every time, never approved silently.</summary>
    public ValueTask<string?> GetConsentTypeAsync(BackdoorOAuthClient application, CancellationToken cancellationToken) =>
        new(ConsentTypes.Explicit);

    public ValueTask<string?> GetDisplayNameAsync(BackdoorOAuthClient application, CancellationToken cancellationToken) =>
        new(application.ClientName);

    public ValueTask<ImmutableDictionary<CultureInfo, string>> GetDisplayNamesAsync(
        BackdoorOAuthClient application, CancellationToken cancellationToken) =>
        new(ImmutableDictionary<CultureInfo, string>.Empty);

    public ValueTask<string?> GetIdAsync(BackdoorOAuthClient application, CancellationToken cancellationToken) =>
        new(application.ClientId);

    public ValueTask<JsonWebKeySet?> GetJsonWebKeySetAsync(BackdoorOAuthClient application, CancellationToken cancellationToken) =>
        new((JsonWebKeySet?)null);

    public ValueTask<ImmutableArray<string>> GetPermissionsAsync(
        BackdoorOAuthClient application, CancellationToken cancellationToken) =>
        new(BackdoorOAuthClients.ClientPermissions);

    public ValueTask<ImmutableArray<string>> GetPostLogoutRedirectUrisAsync(
        BackdoorOAuthClient application, CancellationToken cancellationToken) =>
        new(ImmutableArray<string>.Empty);

    public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(
        BackdoorOAuthClient application, CancellationToken cancellationToken) =>
        new(ImmutableDictionary<string, JsonElement>.Empty);

    public ValueTask<ImmutableArray<string>> GetRedirectUrisAsync(
        BackdoorOAuthClient application, CancellationToken cancellationToken) =>
        new([application.RedirectUri]);

    public ValueTask<ImmutableArray<string>> GetRequirementsAsync(
        BackdoorOAuthClient application, CancellationToken cancellationToken) =>
        new(BackdoorOAuthClients.ClientRequirements);

    public ValueTask<ImmutableDictionary<string, string>> GetSettingsAsync(
        BackdoorOAuthClient application, CancellationToken cancellationToken) =>
        new(ImmutableDictionary<string, string>.Empty);

    public ValueTask<BackdoorOAuthClient> InstantiateAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException(ReadOnly);

    public async IAsyncEnumerable<BackdoorOAuthClient> ListAsync(
        int? count, int? offset, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        foreach (var client in BackdoorOAuthClients.All.Skip(offset ?? 0).Take(count ?? int.MaxValue))
            yield return client;
    }

    public async IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
        Func<IQueryable<BackdoorOAuthClient>, TState, IQueryable<TResult>> query, TState state,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        foreach (var result in query(BackdoorOAuthClients.All.AsQueryable(), state))
            yield return result;
    }

    public ValueTask SetApplicationTypeAsync(BackdoorOAuthClient application, string? type, CancellationToken cancellationToken) =>
        throw new NotSupportedException(ReadOnly);

    public ValueTask SetClientIdAsync(BackdoorOAuthClient application, string? identifier, CancellationToken cancellationToken) =>
        throw new NotSupportedException(ReadOnly);

    public ValueTask SetClientSecretAsync(BackdoorOAuthClient application, string? secret, CancellationToken cancellationToken) =>
        throw new NotSupportedException(ReadOnly);

    public ValueTask SetClientTypeAsync(BackdoorOAuthClient application, string? type, CancellationToken cancellationToken) =>
        throw new NotSupportedException(ReadOnly);

    public ValueTask SetConsentTypeAsync(BackdoorOAuthClient application, string? type, CancellationToken cancellationToken) =>
        throw new NotSupportedException(ReadOnly);

    public ValueTask SetDisplayNameAsync(BackdoorOAuthClient application, string? name, CancellationToken cancellationToken) =>
        throw new NotSupportedException(ReadOnly);

    public ValueTask SetDisplayNamesAsync(
        BackdoorOAuthClient application, ImmutableDictionary<CultureInfo, string> names, CancellationToken cancellationToken) =>
        throw new NotSupportedException(ReadOnly);

    public ValueTask SetJsonWebKeySetAsync(BackdoorOAuthClient application, JsonWebKeySet? set, CancellationToken cancellationToken) =>
        throw new NotSupportedException(ReadOnly);

    public ValueTask SetPermissionsAsync(
        BackdoorOAuthClient application, ImmutableArray<string> permissions, CancellationToken cancellationToken) =>
        throw new NotSupportedException(ReadOnly);

    public ValueTask SetPostLogoutRedirectUrisAsync(
        BackdoorOAuthClient application, ImmutableArray<string> uris, CancellationToken cancellationToken) =>
        throw new NotSupportedException(ReadOnly);

    public ValueTask SetPropertiesAsync(
        BackdoorOAuthClient application, ImmutableDictionary<string, JsonElement> properties, CancellationToken cancellationToken) =>
        throw new NotSupportedException(ReadOnly);

    public ValueTask SetRedirectUrisAsync(
        BackdoorOAuthClient application, ImmutableArray<string> uris, CancellationToken cancellationToken) =>
        throw new NotSupportedException(ReadOnly);

    public ValueTask SetRequirementsAsync(
        BackdoorOAuthClient application, ImmutableArray<string> requirements, CancellationToken cancellationToken) =>
        throw new NotSupportedException(ReadOnly);

    public ValueTask SetSettingsAsync(
        BackdoorOAuthClient application, ImmutableDictionary<string, string> settings, CancellationToken cancellationToken) =>
        throw new NotSupportedException(ReadOnly);

    public ValueTask UpdateAsync(BackdoorOAuthClient application, CancellationToken cancellationToken) =>
        throw new NotSupportedException(ReadOnly);
}
