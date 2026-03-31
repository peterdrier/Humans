using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;

namespace Humans.Web.Controllers;

public class AccountController : Controller
{
    private readonly SignInManager<User> _signInManager;
    private readonly UserManager<User> _userManager;
    private readonly IClock _clock;
    private readonly ILogger<AccountController> _logger;
    private readonly IUserEmailService _userEmailService;
    private readonly IMagicLinkService _magicLinkService;

    public AccountController(
        SignInManager<User> signInManager,
        UserManager<User> userManager,
        IClock clock,
        ILogger<AccountController> logger,
        IUserEmailService userEmailService,
        IMagicLinkService magicLinkService)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _clock = clock;
        _logger = logger;
        _userEmailService = userEmailService;
        _magicLinkService = magicLinkService;
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
            return RedirectToAction(nameof(Login));
        }

        var info = await _signInManager.GetExternalLoginInfoAsync();
        if (info is null)
        {
            _logger.LogWarning("Could not get external login info");
            return RedirectToAction(nameof(Login));
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

            _logger.LogInformation("User logged in with {Provider}", info.LoginProvider);
            return RedirectToLocal(returnUrl);
        }

        if (result.IsLockedOut)
        {
            // The external login is linked to a locked-out account (e.g., a merged source account).
            // Check if the OAuth email belongs to a different, active account and re-link.
            var lockedOutEmail = info.Principal.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
            if (!string.IsNullOrEmpty(lockedOutEmail))
            {
                var activeUser = await _magicLinkService.FindUserByVerifiedEmailAsync(lockedOutEmail);
                if (activeUser is not null &&
                    (activeUser.LockoutEnd is null || activeUser.LockoutEnd <= DateTimeOffset.UtcNow))
                {
                    try
                    {
                        // Remove the stale login from the locked source account
                        var lockedUser = await _userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
                        if (lockedUser is not null && lockedUser.Id != activeUser.Id)
                        {
                            await _userManager.RemoveLoginAsync(lockedUser, info.LoginProvider, info.ProviderKey);
                        }

                        // Add the login to the active target account
                        var linkResult = await _userManager.AddLoginAsync(activeUser, info);
                        if (linkResult.Succeeded)
                        {
                            activeUser.LastLoginAt = _clock.GetCurrentInstant();
                            var pictureUrlClaim = info.Principal.FindFirstValue("urn:google:picture");
                            if (string.IsNullOrEmpty(activeUser.ProfilePictureUrl) && pictureUrlClaim is not null)
                            {
                                activeUser.ProfilePictureUrl = pictureUrlClaim;
                            }
                            await _userManager.UpdateAsync(activeUser);

                            await _signInManager.SignInAsync(activeUser, isPersistent: false);
                            _logger.LogInformation(
                                "Re-linked {Provider} login from locked account to active user {UserId} via email match",
                                info.LoginProvider, activeUser.Id);
                            return RedirectToLocal(returnUrl);
                        }

                        _logger.LogWarning(
                            "Failed to re-link {Provider} to active user {UserId} after lockout: {Errors}",
                            info.LoginProvider, activeUser.Id,
                            string.Join(", ", linkResult.Errors.Select(e => e.Description)));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Error re-linking {Provider} to active user {UserId} after lockout",
                            info.LoginProvider, activeUser.Id);
                    }
                }
            }

            return RedirectToPage("/Account/Lockout");
        }

        // No existing login — try to link to an existing account by email
        var email = info.Principal.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
        var name = info.Principal.FindFirstValue(ClaimTypes.Name);
        var pictureUrl = info.Principal.FindFirstValue("urn:google:picture");

        if (string.IsNullOrEmpty(email))
        {
            _logger.LogWarning("Email not provided by external provider");
            return RedirectToAction(nameof(Login));
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

        // No existing account — create a new one
        var user = new User
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = name ?? email,
            ProfilePictureUrl = pictureUrl,
            CreatedAt = _clock.GetCurrentInstant(),
            LastLoginAt = _clock.GetCurrentInstant()
        };

        var createResult = await _userManager.CreateAsync(user);
        if (createResult.Succeeded)
        {
            createResult = await _userManager.AddLoginAsync(user, info);
            if (createResult.Succeeded)
            {
                // Create OAuth UserEmail record for the login email
                await _userEmailService.AddOAuthEmailAsync(user.Id, email);

                await _signInManager.SignInAsync(user, isPersistent: false);
                _logger.LogInformation("User created an account using {Provider}", info.LoginProvider);
                return RedirectToLocal(returnUrl);
            }
        }

        foreach (var error in createResult.Errors)
        {
            ModelState.AddModelError(string.Empty, error.Description);
        }

        return View(nameof(Login));
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
    public IActionResult MagicLinkSignup(string token, string? returnUrl = null)
    {
        var email = _magicLinkService.VerifySignupToken(token);
        if (email is null)
        {
            return View("MagicLinkError");
        }

        ViewData["ReturnUrl"] = returnUrl;
        ViewData["Email"] = email;
        ViewData["Token"] = token;
        return View("CompleteSignup");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CompleteSignup(string token, string displayName, string? returnUrl = null)
    {
        returnUrl ??= Url.Content("~/");

        var email = _magicLinkService.VerifySignupToken(token);
        if (email is null)
        {
            return View("MagicLinkError");
        }

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
        var user = new User
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? email : displayName.Trim(),
            CreatedAt = now,
            LastLoginAt = now
        };

        var createResult = await _userManager.CreateAsync(user);
        if (!createResult.Succeeded)
        {
            _logger.LogError("Failed to create user via magic link signup: {Errors}",
                string.Join(", ", createResult.Errors.Select(e => e.Description)));
            return View("MagicLinkError");
        }

        // Create UserEmail record via service (non-OAuth, verified, notification target)
        await _userEmailService.AddOAuthEmailAsync(user.Id, email);

        await _signInManager.SignInAsync(user, isPersistent: false);
        _logger.LogInformation("User {UserId} created account via magic link signup", user.Id);

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
