using Humans.Base.Interfaces;
using Humans.Users.Contracts;

namespace Humans.Auth.Contracts;

/// <summary>
/// Service for magic link authentication — login via emailed link.
/// Supports login for existing users (via any verified email) and signup for new users.
/// </summary>
public interface IMagicLinkService : IApplicationService
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
    /// Decodes a signup magic link token and returns the email if valid.
    /// Does <em>not</em> consume the token — this is the read used to render the
    /// signup form, so an email security scanner following the link cannot burn
    /// it. Redemption goes through <see cref="VerifyAndConsumeSignupTokenAsync"/>.
    /// </summary>
    /// <param name="token">The signup token to decode.</param>
    /// <param name="expectedEmail">Optional email for logging on failure.</param>
    /// <returns>The email address if the token is valid; null if expired or invalid.</returns>
    string? VerifySignupToken(string token, string? expectedEmail = null);

    /// <summary>
    /// Verifies a signup magic link token and consumes it, so it works exactly once.
    /// The signup counterpart of <see cref="VerifyLoginTokenAsync"/>; call it at the
    /// point of redemption, never on a GET.
    /// </summary>
    /// <param name="token">The signup token to redeem.</param>
    /// <param name="expectedEmail">Optional email for logging on failure.</param>
    /// <returns>The email address if the token was valid and unused; null if expired, invalid, or already redeemed.</returns>
    Task<string?> VerifyAndConsumeSignupTokenAsync(
        string token, string? expectedEmail = null, CancellationToken ct = default);

    /// <summary>
    /// Un-redeems a signup token consumed by <see cref="VerifyAndConsumeSignupTokenAsync"/>
    /// when the signup it was consumed for failed to create an account. Call it only on
    /// that path: provisioning rolls itself back on failure, so nothing was accomplished
    /// and the person must be able to resubmit the same link rather than wait out its
    /// 15 minutes. No-op if the token was never reserved.
    /// </summary>
    void ReleaseSignupToken(string token);

    /// <summary>
    /// Finds a user by verified <see cref="UserEmail"/>. Used
    /// for account linking (OAuth callback) and signup double-click protection.
    /// </summary>
    Task<User?> FindUserByVerifiedEmailAsync(string email, CancellationToken ct = default);
}
