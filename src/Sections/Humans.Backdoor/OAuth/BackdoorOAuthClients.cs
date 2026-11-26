using System.Collections.Immutable;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Humans.Backdoor.OAuth;

/// <summary>
/// An app that may ask a person to "Sign in with Humans". Public: no secret, since it runs on people's devices. What
/// binds the id to the app is the return address, which a sign-in is only ever sent back to.
/// </summary>
internal sealed record BackdoorOAuthClient(string ClientId, string DisplayName, string RedirectUri);

/// <summary>The clients, registered in code. Nobody registers one at run time.</summary>
internal static class BackdoorOAuthClients
{
    public static readonly BackdoorOAuthClient Claude =
        new("humans-claude", "Claude", "https://claude.ai/api/mcp/auth_callback");

    public static readonly IReadOnlyList<BackdoorOAuthClient> All = [Claude];

    public static BackdoorOAuthClient? Find(string? clientId) =>
        All.FirstOrDefault(c => string.Equals(c.ClientId, clientId, StringComparison.Ordinal));

    /// <summary>Authorization code with PKCE and refresh tokens; nothing else.</summary>
    public static readonly ImmutableArray<string> ClientPermissions =
    [
        OpenIddictConstants.Permissions.Endpoints.Authorization,
        OpenIddictConstants.Permissions.Endpoints.Token,
        OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
        OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
        OpenIddictConstants.Permissions.ResponseTypes.Code,
    ];

    public static readonly ImmutableArray<string> ClientRequirements = [Requirements.Features.ProofKeyForCodeExchange];
}
