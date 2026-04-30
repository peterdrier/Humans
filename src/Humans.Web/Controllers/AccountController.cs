using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Web.Controllers;

public class AccountController : Controller
{
    private readonly SignInManager<User> _signInManager;
    private readonly UserManager<User> _userManager;
    private readonly IClock _clock;
    private readonly ILogger<AccountController> _logger;
    private readonly IUserEmailService _userEmailService;
    private readonly IMagicLinkService _magicLinkService;
    private readonly IAuditLogService _auditLogService;

    public AccountController(
        SignInManager<User> signInManager,
        UserManager<User> userManager,
        IClock clock,
        ILogger<AccountController> logger,
        IUserEmailService userEmailService,
        IMagicLinkService magicLinkService,
        IAuditLogService auditLogService)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _clock = clock;
        _logger = logger;
        _userEmailService = userEmailService;
        _magicLinkService = magicLinkService;
        _auditLogService = auditLogService;
    }

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
        var properties = _signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
        return Challenge(properties, provider);
    }

    [HttpGet]
    public async Task<IActionResult> ExternalLoginCallback(string? returnUrl = null, string? remoteError = null)
    {
        returnUrl ??= Url.Content("~/");

        if (remoteError is not null)
        {
            _logger.LogWarning("External login error: {Error}", remoteError);
            return RedirectToAction(nameof(Login), new { returnUrl, error = "oauth" });
        }

        var info = await _signInManager.GetExternalLoginInfoAsync();
        if (info is null)
        {
            _logger.LogWarning("Could not get external login info");
            return RedirectToAction(nameof(Login), new { returnUrl, error = "oauth" });
        }

        // Log once per sign-in whether the Google avatar URL was provided. We capture the
        // URL on User.ProfilePictureUrl but never render it — see issue #532 and the
        // "Import my Google photo" button on /Profile/Edit.
        var googlePictureClaim = info.Principal.FindFirstValue("urn:google:picture");
        if (!string.IsNullOrEmpty(googlePictureClaim))
        {
            _logger.LogInformation("Google avatar URL captured for {Provider} sign-in", info.LoginProvider);
        }
        else
        {
            _logger.LogInformation("Google avatar URL not captured for {Provider} sign-in (claim missing)", info.LoginProvider);
        }

        // Sign in the user with this external login provider if the user already has a login
        var result = await _signInManager.ExternalLoginSignInAsync(
            info.LoginProvider,
            info.ProviderKey,
            isPersistent: false,
            bypassTwoFactor: true);

        if (result.Succeeded)
        {
            // Update last login time
            var existingUser = await _userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
            if (existingUser is not null)
            {
                existingUser.LastLoginAt = _clock.GetCurrentInstant();
                await _userManager.UpdateAsync(existingUser);
            }

            await DetectAndApplyRenameAsync(info);

            _logger.LogInformation("User logged in with {Provider}", info.LoginProvider);
            return RedirectToLocal(returnUrl);
        }

        // No existing login — try to link to an existing account by email
        var email = info.Principal.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
        var name = info.Principal.FindFirstValue(ClaimTypes.Name);
        var pictureUrl = info.Principal.FindFirstValue("urn:google:picture");

        if (result.IsLockedOut)
        {
            // Lockout-relink: a merge or deletion locked out the source User
            // that owns this OAuth login (UserRepository sets
            // LockoutEnd = MaxValue on merge/anonymize). Move the login to the
            // active User identified by the OAuth claim email so the human
            // can keep signing in via Google. See Auth.md.
            //
            // Wrapped in try/catch because UserManager calls EF Core which can
            // throw DbException / DbUpdateException / timeouts. Without the
            // wrapper, RemoveLoginAsync succeeding then AddLoginAsync throwing
            // would orphan the OAuth login (removed from source, not present
            // on target) and the user's next sign-in would create a fresh
            // duplicate. Catching and falling through to the lockedout
            // redirect leaves the source row intact for retry.
            try
            {
                if (!string.IsNullOrEmpty(email))
                {
                    var lockedSource = await _userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
                    var activeTarget = await _magicLinkService.FindUserByVerifiedEmailAsync(email);
                    if (lockedSource is not null && activeTarget is not null && lockedSource.Id != activeTarget.Id)
                    {
                        var removeResult = await _userManager.RemoveLoginAsync(
                            lockedSource, info.LoginProvider, info.ProviderKey);
                        if (removeResult.Succeeded)
                        {
                            var relinkResult = await _userManager.AddLoginAsync(activeTarget, info);
                            if (relinkResult.Succeeded)
                            {
                                activeTarget.LastLoginAt = _clock.GetCurrentInstant();
                                await _userManager.UpdateAsync(activeTarget);
                                await _signInManager.SignInAsync(activeTarget, isPersistent: false);
                                _logger.LogInformation(
                                    "Relinked {Provider} login from locked source {SourceId} to active target {TargetId}",
                                    info.LoginProvider, lockedSource.Id, activeTarget.Id);
                                return RedirectToLocal(returnUrl);
                            }

                            _logger.LogWarning(
                                "Lockout-relink: AddLoginAsync to {TargetId} failed: {Errors}",
                                activeTarget.Id,
                                string.Join(", ", relinkResult.Errors.Select(e => e.Description)));
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Lockout-relink: RemoveLoginAsync from {SourceId} failed: {Errors}",
                                lockedSource.Id,
                                string.Join(", ", removeResult.Errors.Select(e => e.Description)));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error during lockout-relink for {Provider}; falling through to lockedout redirect",
                    info.LoginProvider);
            }

            return RedirectToAction(nameof(Login), new { returnUrl, error = "lockedout" });
        }

        if (string.IsNullOrEmpty(email))
        {
            _logger.LogWarning("Email not provided by external provider");
            return RedirectToAction(nameof(Login), new { returnUrl, error = "oauth" });
        }

        // Account linking: check if a user already exists with this email
        var existingByEmail = await _magicLinkService.FindUserByVerifiedEmailAsync(email);
        if (existingByEmail is not null)
        {
            try
            {
                var linkResult = await _userManager.AddLoginAsync(existingByEmail, info);
                if (linkResult.Succeeded)
                {
                    existingByEmail.LastLoginAt = _clock.GetCurrentInstant();
                    if (string.IsNullOrEmpty(existingByEmail.ProfilePictureUrl) && pictureUrl is not null)
                    {
                        existingByEmail.ProfilePictureUrl = pictureUrl;
                    }
                    await _userManager.UpdateAsync(existingByEmail);

                    await TrySetProviderForUserEmailAsync(existingByEmail.Id, email, info);

                    await _signInManager.SignInAsync(existingByEmail, isPersistent: false);
                    _logger.LogInformation(
                        "Linked {Provider} login to existing user {UserId} via email match",
                        info.LoginProvider, existingByEmail.Id);
                    return RedirectToLocal(returnUrl);
                }

                _logger.LogWarning(
                    "Failed to link {Provider} to existing user {UserId}: {Errors}",
                    info.LoginProvider, existingByEmail.Id,
                    string.Join(", ", linkResult.Errors.Select(e => e.Description)));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error linking {Provider} to existing user {UserId}, falling through to create new account",
                    info.LoginProvider, existingByEmail.Id);
            }
        }

        // No existing account — create a new one.
        var newUserId = Guid.NewGuid();
        var user = new User
        {
            Id = newUserId,
            DisplayName = name ?? email,
            ProfilePictureUrl = pictureUrl,
            CreatedAt = _clock.GetCurrentInstant(),
            LastLoginAt = _clock.GetCurrentInstant()
        };

        var createResult = await _userManager.CreateAsync(user);
        if (!createResult.Succeeded)
        {
            foreach (var error in createResult.Errors)
                ModelState.AddModelError(string.Empty, error.Description);
            ViewData["ReturnUrl"] = returnUrl;
            return View(nameof(Login));
        }

        // Link the OAuth login. If linking fails after Create succeeded, undo the
        // user creation to avoid an orphan User with no auth method (would be
        // unreachable via either OAuth or magic link). RequireUniqueEmail = false
        // means Identity no longer rejects the partial create on its own — we
        // must clean up explicitly.
        var oauthLinkResult = await _userManager.AddLoginAsync(user, info);
        if (!oauthLinkResult.Succeeded)
        {
            try
            {
                await _userManager.DeleteAsync(user);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to clean up orphan user {UserId} after AddLoginAsync failure for {Provider}",
                    user.Id, info.LoginProvider);
            }

            foreach (var error in oauthLinkResult.Errors)
                ModelState.AddModelError(string.Empty, error.Description);
            ViewData["ReturnUrl"] = returnUrl;
            return View(nameof(Login));
        }

        // Create OAuth UserEmail record for the login email. If this fails
        // after CreateAsync + AddLoginAsync both succeeded, we still have an
        // orphan: the User + AspNetUserLogins row persist, but no UserEmail
        // row exists, so GetEffectiveEmail() returns null and the user
        // becomes un-notifiable. Symmetric to CompleteSignup's cleanup.
        try
        {
            await _userEmailService.AddOAuthEmailAsync(user.Id, email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create UserEmail for OAuth signup {UserId} ({Email}); rolling back user + login",
                user.Id, email);
            try
            {
                await _userManager.DeleteAsync(user);
            }
            catch (Exception deleteEx)
            {
                _logger.LogError(deleteEx,
                    "Failed to clean up orphan user {UserId} after AddOAuthEmailAsync failure",
                    user.Id);
            }
            ModelState.AddModelError(string.Empty,
                "We couldn't finish setting up your account. Please try again.");
            ViewData["ReturnUrl"] = returnUrl;
            return View(nameof(Login));
        }

        await TrySetProviderForUserEmailAsync(user.Id, email, info);

        await _signInManager.SignInAsync(user, isPersistent: false);
        _logger.LogInformation("User created an account using {Provider}", info.LoginProvider);
        return RedirectToLocal(returnUrl);
    }

    private async Task DetectAndApplyRenameAsync(ExternalLoginInfo info)
    {
        try
        {
            var match = await _userEmailService.FindByProviderKeyAsync(
                info.LoginProvider, info.ProviderKey);
            if (match is null)
                return;

            var claimEmail = info.Principal.FindFirstValue(ClaimTypes.Email);
            if (string.IsNullOrEmpty(claimEmail))
                return;

            if (string.Equals(match.Email, claimEmail, StringComparison.OrdinalIgnoreCase))
                return;

            var oldEmail = match.Email;
            await _userEmailService.RewriteEmailAddressAsync(match.UserId, oldEmail, claimEmail);

            await _auditLogService.LogAsync(
                AuditAction.GoogleEmailRenamed,
                nameof(User), match.UserId,
                $"email rename detected: {oldEmail} -> {claimEmail}, sub={info.ProviderKey}",
                nameof(AccountController),
                relatedEntityId: match.Id, relatedEntityType: nameof(UserEmail));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "OAuth rename detection failed for {Provider} sub={Sub}",
                info.LoginProvider, info.ProviderKey);
        }
    }

    private async Task TrySetProviderForUserEmailAsync(Guid userId, string email, ExternalLoginInfo info)
    {
        try
        {
            var rows = await _userEmailService.GetUserEmailsAsync(userId);
            var match = rows.FirstOrDefault(r =>
                string.Equals(r.Email, email, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                _logger.LogWarning(
                    "OAuth provider tag: no UserEmail row matching {Email} found for user {UserId}",
                    email, userId);
                return;
            }

            await _userEmailService.SetProviderAsync(userId, match.Id, info.LoginProvider, info.ProviderKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "OAuth provider tag failed for user {UserId} {Provider} sub={Sub}",
                userId, info.LoginProvider, info.ProviderKey);
        }
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
            await _magicLinkService.SendMagicLinkAsync(email.Trim(), returnUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending magic link for {Email}", email);
        }

        // Always show "check your email" — no account enumeration
        // Calculate expiry time in Europe/Madrid timezone for display
        var madridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var expiryInstant = _clock.GetCurrentInstant() + Duration.FromMinutes(15);
        var expiryLocal = expiryInstant.InZone(madridZone);
        ViewData["ExpiryTime"] = expiryLocal.ToString("HH:mm", null);
        return View("MagicLinkSent");
    }

    [HttpGet]
    public IActionResult MagicLinkConfirm(Guid userId, string token, string? returnUrl = null)
    {
        // Landing page — prevents email security scanners from consuming the token.
        // The actual sign-in happens via POST from the landing page button.
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

        var user = await _magicLinkService.VerifyLoginTokenAsync(userId, token);
        if (user is null)
        {
            return View("MagicLinkError");
        }

        user.LastLoginAt = _clock.GetCurrentInstant();
        await _userManager.UpdateAsync(user);

        await _signInManager.SignInAsync(user, isPersistent: false);
        _logger.LogInformation("User {UserId} logged in via magic link", user.Id);

        return RedirectToLocal(returnUrl);
    }

    [HttpGet]
    public IActionResult MagicLinkSignup(string token, string? email = null, string? returnUrl = null)
    {
        if (string.IsNullOrEmpty(token))
            return View("MagicLinkError");

        var verifiedEmail = _magicLinkService.VerifySignupToken(token, email);
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

        var verifiedEmail = _magicLinkService.VerifySignupToken(token, email);
        if (verifiedEmail is null)
        {
            return View("MagicLinkError");
        }

        email = verifiedEmail;

        // Check if account was already created (double-click protection)
        var existingUser = await _magicLinkService.FindUserByVerifiedEmailAsync(email);
        if (existingUser is not null)
        {
            await _signInManager.SignInAsync(existingUser, isPersistent: false);
            existingUser.LastLoginAt = _clock.GetCurrentInstant();
            await _userManager.UpdateAsync(existingUser);
            return RedirectToLocal(returnUrl);
        }

        var now = _clock.GetCurrentInstant();
        var newUserId = Guid.NewGuid();
        var user = new User
        {
            Id = newUserId,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? email : displayName.Trim(),
            CreatedAt = now,
            LastLoginAt = now
        };

        var createResult = await _userManager.CreateAsync(user);
        if (!createResult.Succeeded)
        {
            _logger.LogError("Failed to create user via magic link signup for {Email}: {Errors}",
                email, string.Join(", ", createResult.Errors.Select(e => e.Description)));
            return View("MagicLinkError");
        }

        // Create the verified UserEmail row. Misnomer alert: AddOAuthEmailAsync
        // sets IsOAuth=true even though this is a magic-link signup, not OAuth.
        // The IsOAuth flag is repurposed in PR 3 (becomes Provider/ProviderKey)
        // and this call site will be revisited then.
        //
        // If creating the UserEmail row fails (race condition, DB error, etc.)
        // delete the just-created User. RequireUniqueEmail = false means
        // Identity no longer rejects partial state on its own; an orphan User
        // with no UserEmail row would be unreachable via either OAuth or
        // magic link. Same pattern as the OAuth callback new-user branch.
        try
        {
            await _userEmailService.AddOAuthEmailAsync(user.Id, email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create UserEmail for magic-link signup {UserId} ({Email}); rolling back user",
                user.Id, email);
            try
            {
                await _userManager.DeleteAsync(user);
            }
            catch (Exception deleteEx)
            {
                _logger.LogError(deleteEx,
                    "Failed to clean up orphan user {UserId} after AddOAuthEmailAsync failure",
                    user.Id);
            }
            return View("MagicLinkError");
        }

        await _signInManager.SignInAsync(user, isPersistent: false);
        _logger.LogInformation("Magic link signup: user {UserId} created account for {Email}", user.Id, email);

        return RedirectToLocal(returnUrl);
    }

    // --- Standard Auth ---

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        _logger.LogInformation("User logged out");
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
