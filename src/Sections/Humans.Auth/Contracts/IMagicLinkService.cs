using Humans.Base.Interfaces;
using Humans.Users.Contracts;

namespace Humans.Auth.Contracts;

public interface IMagicLinkService : IApplicationService
{
    /// <summary>
    /// Sends a login link to an existing user, or a signup link otherwise. Always looks like success (no account
    /// enumeration). One email per 60 seconds per user.
    /// </summary>
    Task SendMagicLinkAsync(string email, string? returnUrl, CancellationToken ct = default);

    /// <summary>
    /// Single-use: consumes the token. False if the user doesn't exist or the token is expired, invalid or used.
    /// </summary>
    Task<bool> VerifyLoginTokenAsync(Guid userId, string token, CancellationToken ct = default);

    /// <summary>
    /// Does not consume the token: this read renders the signup form, so an email scanner following the link
    /// can't burn it. Returns null if expired or invalid; <paramref name="expectedEmail"/> is only for logging.
    /// </summary>
    string? VerifySignupToken(string token, string? expectedEmail = null);

    /// <summary>
    /// Consumes the token, so it works once: call it at redemption, never on a GET. Returns null if expired,
    /// invalid or already redeemed; <paramref name="expectedEmail"/> is only for logging.
    /// </summary>
    Task<string?> VerifyAndConsumeSignupTokenAsync(
        string token, string? expectedEmail = null, CancellationToken ct = default);

    /// <summary>
    /// Call only when the signup the token was consumed for failed to create an account, so the person can
    /// resubmit the same link instead of waiting out its 15 minutes. No-op if never reserved.
    /// </summary>
    void ReleaseSignupToken(string token);
}
