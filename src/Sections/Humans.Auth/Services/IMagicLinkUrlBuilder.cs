using NodaTime;

namespace Humans.Auth.Services;

/// <summary>A decoded link token. Expiry is the caller's check, so an expired link can still name whom it was for.</summary>
internal sealed record MagicLinkToken(string Payload, Instant ExpiresAt);

/// <summary>Exactly one of <paramref name="Token"/> and <paramref name="Problem"/> is set.</summary>
internal sealed record MagicLinkRead(MagicLinkToken? Token, string? Problem)
{
    public static MagicLinkRead Unreadable(string problem) => new(null, problem);
}

internal interface IMagicLinkUrlBuilder
{
    /// <summary>The token expires after 15 minutes.</summary>
    string BuildLoginUrl(Guid userId, string? returnUrl);

    /// <summary>The payload is the user id as a string.</summary>
    MagicLinkRead UnprotectLoginToken(string token);

    /// <summary>The token expires after 15 minutes.</summary>
    string BuildSignupUrl(string email, string? returnUrl);

    /// <summary>The payload is the email.</summary>
    MagicLinkRead UnprotectSignupToken(string token);
}
