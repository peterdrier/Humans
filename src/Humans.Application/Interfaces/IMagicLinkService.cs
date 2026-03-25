namespace Humans.Application.Interfaces;

/// <summary>
/// Service for magic link authentication — login via emailed link.
/// Supports login for existing users (via any verified email) and signup for new users.
/// </summary>
public interface IMagicLinkService
{
    /// <summary>
    /// Sends a magic link login email to an existing user, or a signup link if no user exists.
    /// Always returns success (no account enumeration). Rate-limited to one email per 60 seconds per user.
    /// </summary>
    /// <param name="email">The email address entered on the login form.</param>
    /// <param name="returnUrl">URL to redirect to after login.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendMagicLinkAsync(string email, string? returnUrl, CancellationToken ct = default);

    /// <summary>
    /// Verifies a login magic link token and returns the user if valid.
    /// </summary>
    /// <param name="userId">The user ID from the magic link URL.</param>
    /// <param name="token">The token from the magic link URL.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The user if the token is valid; null if expired or invalid.</returns>
    Task<Humans.Domain.Entities.User?> VerifyLoginTokenAsync(Guid userId, string token, CancellationToken ct = default);

    /// <summary>
    /// Verifies a signup magic link token and returns the email if valid.
    /// </summary>
    /// <param name="token">The DataProtection token from the signup link URL.</param>
    /// <returns>The email address if the token is valid; null if expired or invalid.</returns>
    string? VerifySignupToken(string token);
}
