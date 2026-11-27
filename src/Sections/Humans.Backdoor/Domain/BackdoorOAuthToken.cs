using NodaTime;

namespace Humans.Backdoor.Domain;

/// <summary>
/// OpenIddict's record of an issued code or token. The token itself is never stored here, only enough to revoke it,
/// see a refresh token redeemed once, and prune it once expired.
/// </summary>
internal sealed class BackdoorOAuthToken
{
    public Guid Id { get; set; }

    public Guid? ConnectionId { get; set; }

    public string? ClientId { get; set; }

    public Guid? UserId { get; set; }

    /// <summary>OpenIddict's token type, such as authorization_code, access_token or refresh_token.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>OpenIddict's status: valid, redeemed, revoked, rejected or inactive.</summary>
    public string Status { get; set; } = string.Empty;

    public Instant? CreatedAt { get; set; }

    public Instant? ExpiresAt { get; set; }

    public Instant? RedeemedAt { get; set; }

    /// <summary>Set only for reference tokens, which carry their payload here.</summary>
    public string? ReferenceId { get; set; }

    public string? Payload { get; set; }

    /// <summary>OpenIddict's extra properties, as a JSON object.</summary>
    public string? Properties { get; set; }
}
