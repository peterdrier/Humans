using Humans.Application.Interfaces.Auth;
using Humans.Users.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Users;

// OAuth-callback decision ladder, lifted out of AccountController per HUM0031
// (nobodies-collective/Humans#857) and the A2/A3 findings in
// docs/debt/controller-intent-audit-2026-06-12.md. UserManager allowed per
// design-rules §2a — Identity is a framework concern, and AspNetUserLogins is
// the authoritative store for (Provider, ProviderKey) → UserId.
//
// HUM0005 pins IUserEmailService.ReconcileOAuthIdentityAsync to this class as
// its sole caller. Keep that surface narrow: this service exists only to serve
// the OAuth callback, so nothing else can reach the reconcile primitive.
public sealed class ExternalLoginService(
    UserManager<User> userManager,
    IUserService userService,
    IUserEmailService userEmailService,
    IMagicLinkService magicLinkService,
    IClock clock,
    ILogger<ExternalLoginService> logger) : IExternalLoginService
{
    public async Task<ExternalLoginCompletionResult> CompleteExternalLoginAsync(
        ExternalLoginAttempt attempt,
        CancellationToken ct = default)
    {
        if (attempt.ProviderSignInSucceeded)
            return await CompleteKnownLoginAsync(attempt, ct);

        // Link-while-signed-in must precede lockout/email-match/create — otherwise a fresh OAuth email spawns a duplicate.
        var currentUserLink = await TryLinkToCurrentUserAsync(attempt, ct);
        if (currentUserLink is not null)
            return currentUserLink;

        if (attempt.ProviderSignInLockedOut)
        {
            return await TryRelinkLockedOutAsync(attempt, ct)
                   ?? new ExternalLoginCompletionResult(ExternalLoginOutcome.LockedOut);
        }

        if (string.IsNullOrEmpty(attempt.Email))
        {
            logger.LogWarning("Email not provided by external provider");
            return new ExternalLoginCompletionResult(ExternalLoginOutcome.ProviderError);
        }

        return await TryLinkByVerifiedEmailAsync(attempt, ct)
               ?? await CreateUserAsync(attempt, ct);
    }

    private async Task<ExternalLoginCompletionResult> CompleteKnownLoginAsync(
        ExternalLoginAttempt attempt,
        CancellationToken ct)
    {
        var existingUser = await userManager.FindByLoginAsync(
            attempt.Login.LoginProvider,
            attempt.Login.ProviderKey);
        if (existingUser is not null)
        {
            await userService.RecordLoginAsync(existingUser.Id, ct);
            await TryReconcileAsync(existingUser.Id, attempt, ct);
        }

        logger.LogInformation("User logged in with {Provider}", attempt.Login.LoginProvider);

        // Identity's ExternalLoginSignInAsync already issued the cookie.
        return new ExternalLoginCompletionResult(ExternalLoginOutcome.SignedIn);
    }

    private async Task<ExternalLoginCompletionResult?> TryLinkToCurrentUserAsync(
        ExternalLoginAttempt attempt,
        CancellationToken ct)
    {
        if (attempt.CurrentUserId is not { } currentUserId)
            return null;

        var currentUser = await userManager.FindByIdAsync(currentUserId.ToString());
        if (currentUser is null)
            return null;

        var addLinkResult = await userManager.AddLoginAsync(currentUser, attempt.Login);
        if (addLinkResult.Succeeded)
        {
            await userService.RecordLoginAsync(currentUser.Id, ct);

            if (!string.IsNullOrEmpty(attempt.Email))
                await TryReconcileAsync(currentUser.Id, attempt, ct);

            logger.LogInformation(
                "Linked {Provider} login to currently-authenticated user {UserId}",
                attempt.Login.LoginProvider,
                currentUser.Id);

            // Their session already exists — no cookie to re-issue.
            return new ExternalLoginCompletionResult(ExternalLoginOutcome.SignedIn);
        }

        logger.LogWarning(
            "Failed to link {Provider} to authenticated user {UserId}: {Errors}",
            attempt.Login.LoginProvider,
            currentUser.Id,
            string.Join(", ", addLinkResult.Errors.Select(e => e.Description)));

        return new ExternalLoginCompletionResult(ExternalLoginOutcome.LinkToCurrentUserFailed);
    }

    private async Task<ExternalLoginCompletionResult?> TryRelinkLockedOutAsync(
        ExternalLoginAttempt attempt,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(attempt.Email))
                return null;

            var lockedSource = await userManager.FindByLoginAsync(
                attempt.Login.LoginProvider,
                attempt.Login.ProviderKey);
            var activeTarget = await magicLinkService.FindUserByVerifiedEmailAsync(attempt.Email, ct);
            if (lockedSource is null || activeTarget is null || lockedSource.Id == activeTarget.Id)
                return null;

            var removeResult = await userManager.RemoveLoginAsync(
                lockedSource,
                attempt.Login.LoginProvider,
                attempt.Login.ProviderKey);
            if (!removeResult.Succeeded)
            {
                logger.LogWarning(
                    "Lockout-relink: RemoveLoginAsync from {SourceId} failed: {Errors}",
                    lockedSource.Id,
                    string.Join(", ", removeResult.Errors.Select(e => e.Description)));
                return null;
            }

            var relinkResult = await userManager.AddLoginAsync(activeTarget, attempt.Login);
            if (!relinkResult.Succeeded)
            {
                logger.LogWarning(
                    "Lockout-relink: AddLoginAsync to {TargetId} failed: {Errors}",
                    activeTarget.Id,
                    string.Join(", ", relinkResult.Errors.Select(e => e.Description)));
                return null;
            }

            await userService.RecordLoginAsync(activeTarget.Id, ct);
            await TryReconcileAsync(activeTarget.Id, attempt, ct);

            logger.LogInformation(
                "Relinked {Provider} login from locked source {SourceId} to active target {TargetId}",
                attempt.Login.LoginProvider,
                lockedSource.Id,
                activeTarget.Id);
            return new ExternalLoginCompletionResult(ExternalLoginOutcome.SignedIn, activeTarget);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Error during lockout-relink for {Provider}; falling through to lockedout redirect",
                attempt.Login.LoginProvider);
            return null;
        }
    }

    private async Task<ExternalLoginCompletionResult?> TryLinkByVerifiedEmailAsync(
        ExternalLoginAttempt attempt,
        CancellationToken ct)
    {
        var existingByEmail = await magicLinkService.FindUserByVerifiedEmailAsync(attempt.Email, ct);
        if (existingByEmail is null)
            return null;

        try
        {
            var linkResult = await userManager.AddLoginAsync(existingByEmail, attempt.Login);
            if (linkResult.Succeeded)
            {
                await userService.RecordLoginAsync(existingByEmail.Id, ct);
                await TryReconcileAsync(existingByEmail.Id, attempt, ct);

                logger.LogInformation(
                    "Linked {Provider} login to existing user {UserId} via email match",
                    attempt.Login.LoginProvider,
                    existingByEmail.Id);
                return new ExternalLoginCompletionResult(ExternalLoginOutcome.SignedIn, existingByEmail);
            }

            logger.LogWarning(
                "Failed to link {Provider} to existing user {UserId}: {Errors}",
                attempt.Login.LoginProvider,
                existingByEmail.Id,
                string.Join(", ", linkResult.Errors.Select(e => e.Description)));
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Error linking {Provider} to existing user {UserId}, falling through to create new account",
                attempt.Login.LoginProvider,
                existingByEmail.Id);
        }

        return null;
    }

    private async Task<ExternalLoginCompletionResult> CreateUserAsync(
        ExternalLoginAttempt attempt,
        CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var user = new User
        {
            Id = Guid.NewGuid(),
            DisplayName = attempt.DisplayName ?? attempt.Email,
            CreatedAt = now,
            LastLoginAt = now
        };

        var createResult = await userManager.CreateAsync(user);
        if (!createResult.Succeeded)
            return CreateFailed(createResult.Errors);

        var oauthLinkResult = await userManager.AddLoginAsync(user, attempt.Login);
        if (!oauthLinkResult.Succeeded)
        {
            await TryDeleteOrphanUserAsync(user);
            return CreateFailed(oauthLinkResult.Errors);
        }

        var reconcileFailure = await TryReconcileNewUserAsync(user, attempt, ct);
        if (reconcileFailure is not null)
            return reconcileFailure;

        await userService.EnsureStubProfileAsync(user.Id, ct: ct);

        logger.LogInformation("User created an account using {Provider}", attempt.Login.LoginProvider);
        return new ExternalLoginCompletionResult(ExternalLoginOutcome.SignedIn, user);
    }

    // Reconcile for a just-created account: unlike the sign-in paths this one
    // decides the outcome, because a half-provisioned account must not survive.
    private async Task<ExternalLoginCompletionResult?> TryReconcileNewUserAsync(
        User user,
        ExternalLoginAttempt attempt,
        CancellationToken ct)
    {
        try
        {
            var reconcile = await userEmailService.ReconcileOAuthIdentityAsync(
                user.Id,
                attempt.Login.LoginProvider,
                attempt.Login.ProviderKey,
                attempt.Email,
                claimEmailVerified: attempt.EmailVerified,
                ct);

            if (reconcile.Outcome != ReconcileOutcome.CrossUserBlocked)
                return null;

            await TryDeleteOrphanUserAsync(user);
            return new ExternalLoginCompletionResult(ExternalLoginOutcome.SetupFailed);
        }
        catch (OAuthReconcileConcurrencyException race)
        {
            logger.LogError(race,
                "OAuth signup race on UserEmail unique index for new user " +
                "{UserId} (provider={Provider}, sub={Sub}, claimEmail={Email}); " +
                "rolling back user + login. The verified-email partial unique " +
                "index caught a concurrent insert past the reconcile pre-check " +
                "- investigate via /Profile/Admin/EmailProblems.",
                user.Id,
                attempt.Login.LoginProvider,
                attempt.Login.ProviderKey,
                attempt.Email);
            await TryDeleteOrphanUserAsync(user);
            return new ExternalLoginCompletionResult(ExternalLoginOutcome.SetupFailed);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to reconcile OAuth identity for new user {UserId} ({Email}); rolling back user + login",
                user.Id,
                attempt.Email);
            await TryDeleteOrphanUserAsync(user);
            return new ExternalLoginCompletionResult(ExternalLoginOutcome.SetupFailed);
        }
    }

    // Reconcile wrapper for OAuth-success paths - sign-in never blocks on failure (swallow + log).
    private async Task TryReconcileAsync(Guid userId, ExternalLoginAttempt attempt, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(attempt.Email))
            return;

        try
        {
            await userEmailService.ReconcileOAuthIdentityAsync(
                userId,
                attempt.Login.LoginProvider,
                attempt.Login.ProviderKey,
                attempt.Email,
                claimEmailVerified: attempt.EmailVerified,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OAuthReconcileConcurrencyException race)
        {
            // Verified-email partial unique index caught a concurrent insert (rare race). Log; sign-in continues.
            logger.LogError(race,
                "OAuth reconcile race for user {UserId} " +
                "(provider={Provider}, sub={Sub}, claimEmail={Email}); " +
                "sign-in continues — investigate via /Profile/Admin/EmailProblems.",
                userId, attempt.Login.LoginProvider, attempt.Login.ProviderKey, attempt.Email);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "OAuth reconcile failed for user {UserId} {Provider} sub={Sub}; sign-in continues",
                userId, attempt.Login.LoginProvider, attempt.Login.ProviderKey);
        }
    }

    // Best-effort rollback after OAuth signup fails post-CreateAsync; orphan logged at Error for manual cleanup.
    private async Task TryDeleteOrphanUserAsync(User user)
    {
        try
        {
            await userManager.DeleteAsync(user);
        }
        catch (Exception deleteEx)
        {
            logger.LogError(deleteEx,
                "Failed to clean up orphan user {UserId} after reconcile failure",
                user.Id);
        }
    }

    private static ExternalLoginCompletionResult CreateFailed(IEnumerable<IdentityError> errors) =>
        new(ExternalLoginOutcome.CreateFailed)
        {
            Errors = errors.Select(e => e.Description).ToList()
        };
}
