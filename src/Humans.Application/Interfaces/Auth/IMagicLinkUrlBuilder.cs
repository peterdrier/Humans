using Humans.Application.Interfaces.Users;

namespace Humans.Application.Interfaces.Auth;

/// <summary>
/// Abstracts the Infrastructure-side concerns of magic-link auth: Data
/// Protection token generation/validation and URL construction using
/// <c>EmailSettings.BaseUrl</c>. Lets <see cref="IMagicLinkService"/> live in
/// <c>Humans.Application</c> without referencing <c>IDataProtectionProvider</c>
/// or <c>EmailSettings</c>. Same pattern as
/// <see cref="IUnsubscribeTokenProvider"/>.
/// </summary>
public interface IMagicLinkUrlBuilder
{
    /// <summary>
    /// Protects the given user-id payload with a Data Protection token that
    /// expires after the magic-link lifetime (15 minutes) and returns the
    /// full login URL.
    /// </summary>
    string BuildLoginUrl(Guid userId, string? returnUrl);

    /// <summary>
    /// Attempts to unprotect a login token. Returns the payload (the user id
    /// as a string) if the token is valid and not expired, or null if it is
    /// malformed or expired.
    /// </summary>
    string? UnprotectLoginToken(string token);

    /// <summary>
    /// Protects the given email address with a Data Protection token that
    /// expires after the magic-link lifetime (15 minutes) and returns the
    /// full signup URL.
    /// </summary>
    string BuildSignupUrl(string email, string? returnUrl);

    /// <summary>
    /// Attempts to unprotect a signup token. Returns the email address if the
    /// token is valid and not expired, or null if it is malformed or expired.
    /// </summary>
    string? UnprotectSignupToken(string token);
}
