using Microsoft.AspNetCore.Identity;
using Humans.Base.Interfaces;

namespace Humans.Users.Contracts;

/// <summary>
/// The OAuth callback's decision ladder: which account the identity belongs to, linking, relinking away from a
/// locked-out account, provisioning. The only legal caller of <c>IUserEmailService.ReconcileOAuthIdentityAsync</c>
/// (HUM0005): only here is the provider's assertion in hand.
/// </summary>
public interface IExternalLoginService : IApplicationService
{
    /// <summary>
    /// Performs every mutation the outcome implies. Sign-in never blocks: a reconcile failure on an otherwise
    /// successful path is logged and swallowed; only a half-built account yields
    /// <see cref="ExternalLoginOutcome.SetupFailed"/>.
    /// </summary>
    Task<ExternalLoginCompletionResult> CompleteExternalLoginAsync(
        ExternalLoginAttempt attempt,
        CancellationToken ct = default);
}

/// <summary>What the provider and Identity's own external sign-in reported.</summary>
/// <param name="Login">Passed to <c>AddLoginAsync</c> unchanged, so the stored row keeps the provider's display name.</param>
/// <param name="Email">Empty when the provider sent no email claim.</param>
/// <param name="DisplayName">Seeds a new account.</param>
/// <param name="EmailVerified">OIDC <c>email_verified</c>; false when missing or unparseable.</param>
/// <param name="ProviderSignInSucceeded">Identity matched an existing login row and signed the user in.</param>
/// <param name="ProviderSignInLockedOut">Identity matched a row but its account is locked out.</param>
/// <param name="CurrentUserId">The signed-in user, for an add-a-login flow.</param>
public sealed record ExternalLoginAttempt(
    UserLoginInfo Login,
    string Email,
    string? DisplayName,
    bool EmailVerified,
    bool ProviderSignInSucceeded,
    bool ProviderSignInLockedOut,
    Guid? CurrentUserId);

/// <summary><c>SignInUser</c> is set when the caller must issue the cookie; null when a session already exists or the attempt failed.</summary>
public sealed record ExternalLoginCompletionResult(
    ExternalLoginOutcome Outcome,
    User? SignInUser = null)
{
    /// <summary>Identity error descriptions for <see cref="ExternalLoginOutcome.CreateFailed"/>.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}

public enum ExternalLoginOutcome
{
    SignedIn,

    /// <summary>Adding the provider to the already-signed-in user failed; their session is untouched.</summary>
    LinkToCurrentUserFailed,

    /// <summary>The matched account is locked out and no active account claims the address.</summary>
    LockedOut,

    /// <summary>No email claim.</summary>
    ProviderError,

    /// <summary>Identity refused to create the account; <see cref="ExternalLoginCompletionResult.Errors"/> says why.</summary>
    CreateFailed,

    /// <summary>Created but couldn't be finished, and rolled back.</summary>
    SetupFailed,
}
