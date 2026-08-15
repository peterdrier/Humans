using Microsoft.AspNetCore.Identity;
using Humans.Users.Contracts;

namespace Humans.Application.Interfaces.Users;

/// <summary>
/// Owns the decision ladder of the external (OAuth) sign-in callback: which
/// existing account the asserted identity belongs to, whether to link it to a
/// signed-in user, whether to relink it away from a locked-out account, and
/// when to provision a new account. The MVC action stays session mechanics —
/// it obtains <see cref="ExternalLoginInfo"/>, runs Identity's external
/// sign-in, dispatches here, then issues the cookie and picks a redirect.
/// <para>
/// This service is the sole legal caller of
/// <c>IUserEmailService.ReconcileOAuthIdentityAsync</c> (analyzer HUM0005) —
/// it is the only place holding the authoritative
/// <c>(userId, provider, providerKey, claimEmail, claimEmailVerified)</c>
/// quintuple in the moment the provider asserts it. See
/// <c>memory/architecture/email-mutation-paths.md</c>.
/// </para>
/// </summary>
public interface IExternalLoginService : IApplicationService
{
    /// <summary>
    /// Resolves an OAuth callback to its account outcome, performing every
    /// account mutation the outcome implies (login linking, relinking,
    /// provisioning, email reconcile, rollback). Sign-in never blocks: a
    /// reconcile failure on an otherwise-successful path is logged and
    /// swallowed, and only a failure that would leave a half-built account
    /// produces <see cref="ExternalLoginOutcome.SetupFailed"/>.
    /// </summary>
    Task<ExternalLoginCompletionResult> CompleteExternalLoginAsync(
        ExternalLoginAttempt attempt,
        CancellationToken ct = default);
}

/// <summary>
/// Everything the callback learned from the provider and from Identity's own
/// external sign-in attempt.
/// </summary>
/// <param name="Login">
/// The provider identity as Identity models it. Passed through to
/// <c>UserManager.AddLoginAsync</c> unchanged so the stored
/// <c>AspNetUserLogins</c> row keeps the provider's display name verbatim.
/// </param>
/// <param name="Email">The provider's email claim; empty when it sent none.</param>
/// <param name="DisplayName">The provider's name claim, used to seed a new account.</param>
/// <param name="EmailVerified">The OIDC <c>email_verified</c> claim; false when missing or unparseable.</param>
/// <param name="ProviderSignInSucceeded">Identity matched an existing <c>AspNetUserLogins</c> row and signed the user in.</param>
/// <param name="ProviderSignInLockedOut">Identity matched a row but the owning account is locked out.</param>
/// <param name="CurrentUserId">The already-signed-in user, when the callback is an add-a-login flow.</param>
public sealed record ExternalLoginAttempt(
    UserLoginInfo Login,
    string Email,
    string? DisplayName,
    bool EmailVerified,
    bool ProviderSignInSucceeded,
    bool ProviderSignInLockedOut,
    Guid? CurrentUserId);

/// <summary>
/// Result of completing an external-login attempt: the outcome the controller
/// must act on, plus the user to sign in when a cookie must be issued.
/// </summary>
/// <param name="Outcome">What the caller must do next.</param>
/// <param name="SignInUser">
/// Set when the caller must issue the auth cookie for this user. Null when the
/// session already exists (Identity signed them in, or they were signed in
/// already) or when the attempt failed.
/// </param>
public sealed record ExternalLoginCompletionResult(
    ExternalLoginOutcome Outcome,
    User? SignInUser = null)
{
    /// <summary>Identity error descriptions for <see cref="ExternalLoginOutcome.CreateFailed"/>.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}

public enum ExternalLoginOutcome
{
    /// <summary>The account is resolved; send them on to their destination.</summary>
    SignedIn,

    /// <summary>Adding the provider to the already-signed-in user failed; their session is untouched.</summary>
    LinkToCurrentUserFailed,

    /// <summary>The matched account is locked out and no active account claims the address.</summary>
    LockedOut,

    /// <summary>The provider sent no usable identity — no email claim.</summary>
    ProviderError,

    /// <summary>Identity refused to create the account; <see cref="ExternalLoginCompletionResult.Errors"/> says why.</summary>
    CreateFailed,

    /// <summary>The account was created but could not be finished, and was rolled back.</summary>
    SetupFailed,
}
