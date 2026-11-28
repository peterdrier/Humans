using Humans.Base.Interfaces;

namespace Humans.Auth.Contracts;

public interface ISignInLinkService : IApplicationService
{
    /// <summary>
    /// Sends a sign-in link to an existing user, or a signup link otherwise. Always looks like success (no account
    /// enumeration). One email per 60 seconds per user.
    /// </summary>
    Task SendSignInLinkAsync(string email, string? returnUrl, CancellationToken ct = default);

    /// <summary>Single-use: consumes the token. Null when it signs the user in, else the one reason it does not.</summary>
    Task<SignInLinkFailure?> VerifySignInTokenAsync(Guid userId, string token, CancellationToken ct = default);

    /// <summary>
    /// Does not consume the token: this read renders the signup form, so an email scanner following the link
    /// can't burn it. <paramref name="expectedEmail"/> is only for logging.
    /// </summary>
    SignInLinkSignupCheck VerifySignupToken(string token, string? expectedEmail = null);

    /// <summary>
    /// Consumes the token, so it works once: call it at redemption, never on a GET.
    /// <paramref name="expectedEmail"/> is only for logging.
    /// </summary>
    Task<SignInLinkSignupCheck> ConsumeSignupTokenAsync(string token, string? expectedEmail = null);

    /// <summary>
    /// Call only when the signup the token was consumed for failed to create an account, so the person can
    /// resubmit the same link instead of waiting out its 15 minutes. No-op if never reserved.
    /// </summary>
    void ReleaseSignupToken(string token);
}

/// <summary>Why a sign-in link does not sign someone in or up.</summary>
public enum SignInLinkFailure
{
    /// <summary>The token could not be read, or names a different account than the link does.</summary>
    Unreadable,
    Expired,
    AlreadyUsed,
    AccountNotFound,
}

/// <summary>A signup link checked: exactly one of <paramref name="Email"/> and <paramref name="Failure"/> is set.</summary>
public sealed record SignInLinkSignupCheck(string? Email, SignInLinkFailure? Failure);
