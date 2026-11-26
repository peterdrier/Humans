using NodaTime;

namespace Humans.Auth.Services;

/// <summary>A decoded link token. Expiry is the caller's check, so an expired link can still name whom it was for.</summary>
internal sealed record SignInLinkToken(string Payload, Instant ExpiresAt);

internal interface ISignInLinkUrlBuilder
{
    /// <summary>The token expires after 15 minutes.</summary>
    string BuildSignInUrl(Guid userId, string? returnUrl);

    /// <summary>The payload is the user id as a string. Null if malformed or not issued by us.</summary>
    SignInLinkToken? UnprotectSignInToken(string token);

    /// <summary>The token expires after 15 minutes.</summary>
    string BuildSignupUrl(string email, string? returnUrl);

    /// <summary>The payload is the email. Null if malformed or not issued by us.</summary>
    SignInLinkToken? UnprotectSignupToken(string token);
}
