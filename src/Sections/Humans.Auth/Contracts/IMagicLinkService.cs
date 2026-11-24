using Humans.Base.Interfaces;

namespace Humans.Auth.Contracts;

public interface IMagicLinkService : IApplicationService
{
    /// <summary>
    /// Sends a login link to an existing user, or a signup link otherwise. Always looks like success (no account
    /// enumeration). One email per 60 seconds per user.
    /// </summary>
    Task SendMagicLinkAsync(string email, string? returnUrl, CancellationToken ct = default);

    /// <summary>Single-use: consumes the token. Null when it signs the user in, else the one reason it does not.</summary>
    Task<MagicLinkFailure?> VerifyLoginTokenAsync(Guid userId, string token, CancellationToken ct = default);

    /// <summary>
    /// Does not consume the token: this read renders the signup form, so an email scanner following the link
    /// can't burn it. <paramref name="expectedEmail"/> is only for logging.
    /// </summary>
    MagicLinkSignupCheck VerifySignupToken(string token, string? expectedEmail = null);

    /// <summary>
    /// Consumes the token, so it works once: call it at redemption, never on a GET.
    /// <paramref name="expectedEmail"/> is only for logging.
    /// </summary>
    Task<MagicLinkSignupCheck> VerifyAndConsumeSignupTokenAsync(
        string token, string? expectedEmail = null, CancellationToken ct = default);

    /// <summary>
    /// Call only when the signup the token was consumed for failed to create an account, so the person can
    /// resubmit the same link instead of waiting out its 15 minutes. No-op if never reserved.
    /// </summary>
    void ReleaseSignupToken(string token);
}

/// <summary>Why a magic link does not sign someone in or up.</summary>
public enum MagicLinkFailure
{
    /// <summary>The token could not be read, or names a different account than the link does.</summary>
    Unreadable,
    Expired,
    AlreadyUsed,
    AccountNotFound,
}

/// <summary>A signup link checked: exactly one of <paramref name="Email"/> and <paramref name="Failure"/> is set.</summary>
public sealed record MagicLinkSignupCheck(string? Email, MagicLinkFailure? Failure);
