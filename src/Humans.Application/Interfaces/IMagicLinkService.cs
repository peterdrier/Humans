using Humans.Domain.Entities;

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
    Task SendMagicLinkAsync(string email, string? returnUrl, CancellationToken ct = default);

    /// <summary>
    /// Verifies a login magic link token and returns the user if valid.
    /// Tokens are single-use (consumed on verification).
    /// </summary>
    /// <returns>The user if the token is valid; null if expired, invalid, or already used.</returns>
    Task<User?> VerifyLoginTokenAsync(Guid userId, string token, CancellationToken ct = default);

    /// <summary>
    /// Verifies a signup magic link token and returns the email if valid.
    /// </summary>
    /// <returns>The email address if the token is valid; null if expired or invalid.</returns>
    string? VerifySignupToken(string token);

    /// <summary>
    /// Finds a user by checking verified UserEmails first, then User.NormalizedEmail.
    /// Used for account linking (OAuth callback) and signup double-click protection.
    /// </summary>
    Task<User?> FindUserByVerifiedEmailAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Finds a user by checking ANY UserEmail (including unverified) and User.Email/NormalizedEmail.
    /// Used to prevent duplicate account creation during OAuth login when the email exists
    /// as an unverified UserEmail on another account.
    /// </summary>
    Task<User?> FindUserByAnyEmailAsync(string email, CancellationToken ct = default);
}
