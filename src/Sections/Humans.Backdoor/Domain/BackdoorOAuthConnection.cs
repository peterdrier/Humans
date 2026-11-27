using NodaTime;

namespace Humans.Backdoor.Domain;

/// <summary>
/// One person's "Sign in with Humans" approval for a client: OpenIddict's authorization. Revoking it stops every token
/// issued under it on the next call.
/// </summary>
internal sealed class BackdoorOAuthConnection
{
    public Guid Id { get; set; }

    /// <summary>The human every token under this connection acts as.</summary>
    public Guid UserId { get; set; }

    /// <summary>The client id, from <see cref="OAuth.BackdoorOAuthClients"/>.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>OpenIddict's status: valid or revoked.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>OpenIddict's type: permanent or ad-hoc.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Space separated.</summary>
    public string Scopes { get; set; } = string.Empty;

    public Instant CreatedAt { get; set; }

    /// <summary>OpenIddict's extra properties, as a JSON object.</summary>
    public string? Properties { get; set; }
}
