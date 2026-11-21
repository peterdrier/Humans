using NodaTime;

namespace Humans.Auth.Services;

/// <summary>A decoded link token. Expiry is the caller's check, so an expired link can still name whom it was for.</summary>
internal sealed record MagicLinkToken(string Payload, Instant ExpiresAt);

internal interface IMagicLinkUrlBuilder
{
    /// <summary>The token expires after 15 minutes.</summary>
    string BuildLoginUrl(Guid userId, string? returnUrl);

    /// <summary>The payload is the user id as a string. Null if malformed or not issued by us.</summary>
    MagicLinkToken? UnprotectLoginToken(string token);

    /// <summary>The token expires after 15 minutes.</summary>
    string BuildSignupUrl(string email, string? returnUrl);

    /// <summary>The payload is the email. Null if malformed or not issued by us.</summary>
    MagicLinkToken? UnprotectSignupToken(string token);
}
