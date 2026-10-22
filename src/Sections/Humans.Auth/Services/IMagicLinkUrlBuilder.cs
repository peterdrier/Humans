namespace Humans.Auth.Services;

internal interface IMagicLinkUrlBuilder
{
    /// <summary>The token expires after 15 minutes.</summary>
    string BuildLoginUrl(Guid userId, string? returnUrl);

    /// <summary>Returns the user id as a string, or null if malformed or expired.</summary>
    string? UnprotectLoginToken(string token);

    /// <summary>The token expires after 15 minutes.</summary>
    string BuildSignupUrl(string email, string? returnUrl);

    /// <summary>Returns the email, or null if malformed or expired.</summary>
    string? UnprotectSignupToken(string token);
}
