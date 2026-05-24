using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;
using Humans.Domain.Entities;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

public class AccountController(
    SignInManager<User> signInManager,
    IUserServiceRead userService,
    UserManager<User> userManager,
    IClock clock,
    ILogger<AccountController> logger,
    IUserEmailService userEmailService,
    IMagicLinkService magicLinkService,
    IAccountProvisioningService accountProvisioningService,
    IProfileService profileService,
    IStringLocalizer<SharedResource> localizer) : HumansControllerBase(userService)
{
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ExternalLogin(string provider, string? returnUrl = null)
    {
        var redirectUrl = Url.Action(nameof(ExternalLoginCallback), "Account", new { returnUrl });
        var properties = signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
        return Challenge(properties, provider);
    }

    [HttpGet]
    public async Task<IActionResult> ExternalLoginCallback(string? returnUrl = null, string? remoteError = null)
    {
        returnUrl ??= Url.Content("~/");

        if (remoteError is not null)
        {
            logger.LogWarning("External login error: {Error}", remoteError);
            return RedirectToAction(nameof(Login), new { returnUrl, error = "oauth" });
        }

        var info = await signInManager.GetExternalLoginInfoAsync();
        if (info is null)
        {
            logger.LogWarning("Could not get external login info");
            return RedirectToAction(nameof(Login), new { returnUrl, error = "oauth" });
        }

        // see #532 — capture Google avatar URL but never render it; user opts in via "Import my Google photo".
        var googlePictureClaim = info.Principal.FindFirstValue("urn:google:picture");
        if (!string.IsNullOrEmpty(googlePictureClaim))
        {
            logger.LogInformation("Google avatar URL captured for {Provider} sign-in", info.LoginProvider);
        }
        else
        {
            logger.LogInformation("Google avatar URL not captured for {Provider} sign-in (claim missing)", info.LoginProvider);
        }

        var result = await signInManager.ExternalLoginSignInAsync(
            info.LoginProvider,
            info.ProviderKey,
            isPersistent: false,
            bypassTwoFactor: true);

        if (result.Succeeded)
        {
            var existingUser = await userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
            if (existingUser is not null)
            {
                existingUser.LastLoginAt = clock.GetCurrentInstant();
                await userManager.UpdateAsync(existingUser);

                await TryReconcileOAuthIdentityAsync(existingUser.Id, info);
            }

            logger.LogInformation("User logged in with {Provider}", info.LoginProvider);
            return RedirectToLocal(returnUrl);
        }

        var email = info.Principal.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
        var name = info.Principal.FindFirstValue(ClaimTypes.Name);
        var pictureUrl = info.Principal.FindFirstValue("urn:google:picture");

        // Link-while-signed-in must precede lockout/email-match/create — otherwise a fresh OAuth email spawns a duplicate.
        if (User.Identity?.IsAuthenticated == true)
        {
            var currentUser = await userManager.GetUserAsync(User);
            if (currentUser is not null)
            {
                var addLinkResult = await userManager.AddLoginAsync(currentUser, info);
                if (addLinkResult.Succeeded)
                {
                    currentUser.LastLoginAt = clock.GetCurrentInstant();
                    if (string.IsNullOrEmpty(currentUser.ProfilePictureUrl) && pictureUrl is not null)
                    {
                        currentUser.ProfilePictureUrl = pictureUrl;
                    }
                    await userManager.UpdateAsync(currentUser);

                    if (!string.IsNullOrEmpty(email))
                    {
                        await TryReconcileOAuthIdentityAsync(currentUser.Id, info);
                    }

                    logger.LogInformation(
                        "Linked {Provider} login to currently-authenticated user {UserId}",
                        info.LoginProvider, currentUser.Id);
                    return RedirectToLocal(returnUrl);
                }

                logger.LogWarning(
                    "Failed to link {Provider} to authenticated user {UserId}: {Errors}",
                    info.LoginProvider, currentUser.Id,
                    string.Join(", ", addLinkResult.Errors.Select(e => e.Description)));

                // Don't fall through — unauthenticated branches would spawn a duplicate User. Surface as error toast.
                SetError(localizer["EmailGrid_LinkFailed"].Value);
                return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/Profile/Me/Emails");
            }
        }

        if (result.IsLockedOut)
        {
            // Lockout-relink: move OAuth login from merged/anonymized source to active target (see Auth.md). Try/catch so a mid-step throw doesn't orphan the login.
            try
            {
                if (!string.IsNullOrEmpty(email))
                {
                    var lockedSource = await userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
                    var activeTarget = await magicLinkService.FindUserByVerifiedEmailAsync(email);
                    if (lockedSource is not null && activeTarget is not null && lockedSource.Id != activeTarget.Id)
                    {
                        var removeResult = await userManager.RemoveLoginAsync(
                            lockedSource, info.LoginProvider, info.ProviderKey);
                        if (removeResult.Succeeded)
                        {
                            var relinkResult = await userManager.AddLoginAsync(activeTarget, info);
                            if (relinkResult.Succeeded)
                            {
                                activeTarget.LastLoginAt = clock.GetCurrentInstant();
                                await userManager.UpdateAsync(activeTarget);
                                await signInManager.SignInAsync(activeTarget, isPersistent: false);

                                await TryReconcileOAuthIdentityAsync(activeTarget.Id, info);

                                logger.LogInformation(
                                    "Relinked {Provider} login from locked source {SourceId} to active target {TargetId}",
                                    info.LoginProvider, lockedSource.Id, activeTarget.Id);
                                return RedirectToLocal(returnUrl);
                            }

                            logger.LogWarning(
                                "Lockout-relink: AddLoginAsync to {TargetId} failed: {Errors}",
                                activeTarget.Id,
                                string.Join(", ", relinkResult.Errors.Select(e => e.Description)));
                        }
                        else
                        {
                            logger.LogWarning(
                                "Lockout-relink: RemoveLoginAsync from {SourceId} failed: {Errors}",
                                lockedSource.Id,
                                string.Join(", ", removeResult.Errors.Select(e => e.Description)));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Error during lockout-relink for {Provider}; falling through to lockedout redirect",
                    info.LoginProvider);
            }

            return RedirectToAction(nameof(Login), new { returnUrl, error = "lockedout" });
        }

        if (string.IsNullOrEmpty(email))
        {
            logger.LogWarning("Email not provided by external provider");
            return RedirectToAction(nameof(Login), new { returnUrl, error = "oauth" });
        }

        var existingByEmail = await magicLinkService.FindUserByVerifiedEmailAsync(email);
        if (existingByEmail is not null)
        {
            try
            {
                var linkResult = await userManager.AddLoginAsync(existingByEmail, info);
                if (linkResult.Succeeded)
                {
                    existingByEmail.LastLoginAt = clock.GetCurrentInstant();
                    if (string.IsNullOrEmpty(existingByEmail.ProfilePictureUrl) && pictureUrl is not null)
                    {
                        existingByEmail.ProfilePictureUrl = pictureUrl;
                    }
                    await userManager.UpdateAsync(existingByEmail);

                    await TryReconcileOAuthIdentityAsync(existingByEmail.Id, info);

                    await signInManager.SignInAsync(existingByEmail, isPersistent: false);
                    logger.LogInformation(
                        "Linked {Provider} login to existing user {UserId} via email match",
                        info.LoginProvider, existingByEmail.Id);
                    return RedirectToLocal(returnUrl);
                }

                logger.LogWarning(
                    "Failed to link {Provider} to existing user {UserId}: {Errors}",
                    info.LoginProvider, existingByEmail.Id,
                    string.Join(", ", linkResult.Errors.Select(e => e.Description)));
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Error linking {Provider} to existing user {UserId}, falling through to create new account",
                    info.LoginProvider, existingByEmail.Id);
            }
        }

        var newUserId = Guid.NewGuid();
#pragma warning disable HUM_USER_DISPLAYNAME // OAuth signup seeds the legacy Identity fallback column.
        var user = new User
        {
            Id = newUserId,
            DisplayName = name ?? email,
            ProfilePictureUrl = pictureUrl,
            CreatedAt = clock.GetCurrentInstant(),
            LastLoginAt = clock.GetCurrentInstant()
        };
#pragma warning restore HUM_USER_DISPLAYNAME

        var createResult = await userManager.CreateAsync(user);
        if (!createResult.Succeeded)
        {
            foreach (var error in createResult.Errors)
                ModelState.AddModelError(string.Empty, error.Description);
            ViewData["ReturnUrl"] = returnUrl;
            return View(nameof(Login));
        }

        // Roll back the just-created User on link failure — RequireUniqueEmail=false leaves us responsible for cleanup.
        var oauthLinkResult = await userManager.AddLoginAsync(user, info);
        if (!oauthLinkResult.Succeeded)
        {
            try
            {
                await userManager.DeleteAsync(user);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to clean up orphan user {UserId} after AddLoginAsync failure for {Provider}",
                    user.Id, info.LoginProvider);
            }

            foreach (var error in oauthLinkResult.Errors)
                ModelState.AddModelError(string.Empty, error.Description);
            ViewData["ReturnUrl"] = returnUrl;
            return View(nameof(Login));
        }

        // Reconcile creates the UserEmail row; on throw or CrossUserBlocked, roll back the User to avoid an unreachable orphan.
        try
        {
            var reconcile = await userEmailService.ReconcileOAuthIdentityAsync(
                user.Id,
                info.LoginProvider,
                info.ProviderKey,
                email,
                claimEmailVerified: ReadEmailVerifiedClaim(info));

            // CrossUserBlocked: service already audited + logged; roll back the orphan User + login.
            if (reconcile.Outcome == ReconcileOutcome.CrossUserBlocked)
            {
                await TryDeleteOrphanUserAsync(user);
                ModelState.AddModelError(string.Empty,
                    "We couldn't finish setting up your account. Please try again.");
                ViewData["ReturnUrl"] = returnUrl;
                return View(nameof(Login));
            }
        }
        catch (OAuthReconcileConcurrencyException race)
        {
            logger.LogError(race,
                "OAuth signup race on UserEmail unique index for new user " +
                "{UserId} (provider={Provider}, sub={Sub}, claimEmail={Email}); " +
                "rolling back user + login. The verified-email partial unique " +
                "index caught a concurrent insert past the reconcile pre-check " +
                "— investigate via /Profile/Admin/EmailProblems.",
                user.Id, info.LoginProvider, info.ProviderKey, email);
            await TryDeleteOrphanUserAsync(user);
            ModelState.AddModelError(string.Empty,
                "We couldn't finish setting up your account. Please try again.");
            ViewData["ReturnUrl"] = returnUrl;
            return View(nameof(Login));
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to reconcile OAuth identity for new user {UserId} ({Email}); rolling back user + login",
                user.Id, email);
            await TryDeleteOrphanUserAsync(user);
            ModelState.AddModelError(string.Empty,
                "We couldn't finish setting up your account. Please try again.");
            ViewData["ReturnUrl"] = returnUrl;
            return View(nameof(Login));
        }

        // see #635 (§15i) — Stub Profile invariant.
        await profileService.EnsureStubProfileAsync(user.Id);

        await signInManager.SignInAsync(user, isPersistent: false);
        logger.LogInformation("User created an account using {Provider}", info.LoginProvider);
        return RedirectToLocal(returnUrl);
    }

    // Reconcile wrapper for OAuth-success paths — sign-in never blocks on failure (swallow + log).
    private async Task TryReconcileOAuthIdentityAsync(Guid userId, ExternalLoginInfo info)
    {
        var claimEmail = info.Principal.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrEmpty(claimEmail))
            return;

        try
        {
            await userEmailService.ReconcileOAuthIdentityAsync(
                userId,
                info.LoginProvider,
                info.ProviderKey,
                claimEmail,
                claimEmailVerified: ReadEmailVerifiedClaim(info));
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
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
                userId, info.LoginProvider, info.ProviderKey, claimEmail);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "OAuth reconcile failed for user {UserId} {Provider} sub={Sub}; sign-in continues",
                userId, info.LoginProvider, info.ProviderKey);
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

    // OIDC email_verified — missing/unparseable returns false (displacement gate then refuses to displace).
    private static bool ReadEmailVerifiedClaim(ExternalLoginInfo info)
    {
        var raw = info.Principal.FindFirstValue("email_verified");
        return bool.TryParse(raw, out var verified) && verified;
    }

    // --- Magic Link Auth ---

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MagicLinkRequest(string email, string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return RedirectToAction(nameof(Login), new { returnUrl });
        }

        try
        {
            await magicLinkService.SendMagicLinkAsync(email.Trim(), returnUrl);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending magic link for {Email}", email);
        }

        // Always show "check your email" — no account enumeration.
        var madridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var expiryInstant = clock.GetCurrentInstant() + Duration.FromMinutes(15);
        var expiryLocal = expiryInstant.InZone(madridZone);
        ViewData["ExpiryTime"] = expiryLocal.ToString("HH:mm", null);
        return View("MagicLinkSent");
    }

    [HttpGet]
    public IActionResult MagicLinkConfirm(Guid userId, string token, string? returnUrl = null)
    {
        // Landing page prevents email scanners from consuming the token; sign-in is POST.
        ViewData["UserId"] = userId;
        ViewData["Token"] = token;
        ViewData["ReturnUrl"] = returnUrl;
        return View("MagicLinkConfirm");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MagicLink(Guid userId, string token, string? returnUrl = null)
    {
        returnUrl ??= Url.Content("~/");

        var user = await magicLinkService.VerifyLoginTokenAsync(userId, token);
        if (user is null)
        {
            return View("MagicLinkError");
        }

        user.LastLoginAt = clock.GetCurrentInstant();
        await userManager.UpdateAsync(user);

        await signInManager.SignInAsync(user, isPersistent: false);
        logger.LogInformation("User {UserId} logged in via magic link", user.Id);

        return RedirectToLocal(returnUrl);
    }

    [HttpGet]
    public IActionResult MagicLinkSignup(string token, string? email = null, string? returnUrl = null)
    {
        if (string.IsNullOrEmpty(token))
            return View("MagicLinkError");

        var verifiedEmail = magicLinkService.VerifySignupToken(token, email);
        if (verifiedEmail is null)
        {
            return View("MagicLinkError");
        }

        ViewData["ReturnUrl"] = returnUrl;
        ViewData["Email"] = verifiedEmail;
        ViewData["Token"] = token;
        return View("CompleteSignup");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CompleteSignup(string token, string displayName, string? email = null, string? returnUrl = null)
    {
        if (string.IsNullOrEmpty(token))
            return View("MagicLinkError");

        returnUrl ??= Url.Content("~/");

        var verifiedEmail = magicLinkService.VerifySignupToken(token, email);
        if (verifiedEmail is null)
        {
            return View("MagicLinkError");
        }

        var result = await accountProvisioningService.CompleteMagicLinkSignupAsync(
            verifiedEmail,
            displayName,
            HttpContext.RequestAborted);

#pragma warning disable CS0618 // result.User is a record field on MagicLinkSignupCompletionResult, not a cross-domain nav read; arch test pattern-matches the literal `.User`.
        if (result.User is null)
            return View("MagicLinkError");

        await signInManager.SignInAsync(result.User, isPersistent: false);
#pragma warning restore CS0618

        return RedirectToLocal(returnUrl);
    }

    // --- Standard Auth ---

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
        logger.LogInformation("User logged out");
        return RedirectToAction(nameof(HomeController.Index), "Home");
    }

    [HttpGet]
    public IActionResult AccessDenied()
    {
        return View();
    }

    private IActionResult RedirectToLocal(string? returnUrl) =>
        Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl!)
            : Redirect(Url.Content("~/"));
}
