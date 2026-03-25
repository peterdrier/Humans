using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Helpers;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Web.Controllers;

public class AccountController : Controller
{
    private readonly SignInManager<User> _signInManager;
    private readonly UserManager<User> _userManager;
    private readonly IClock _clock;
    private readonly ILogger<AccountController> _logger;
    private readonly IUserEmailService _userEmailService;
    private readonly IContactService _contactService;

    public AccountController(
        SignInManager<User> signInManager,
        UserManager<User> userManager,
        IClock clock,
        ILogger<AccountController> logger,
        IUserEmailService userEmailService,
        IContactService contactService)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _clock = clock;
        _logger = logger;
        _userEmailService = userEmailService;
        _contactService = contactService;
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

                // Auto-merge if a contact exists with the same email
                var loginEmail = info.Principal.FindFirstValue(ClaimTypes.Email);
                if (!string.IsNullOrEmpty(loginEmail))
                {
                    var existingContact = await _contactService.FindContactByEmailAsync(loginEmail);
                    if (existingContact is not null)
                    {
                        await _contactService.MergeContactToMemberAsync(
                            existingContact, existingUser, null, "OAuth login auto-merge");
                    }
                }
            }

            _logger.LogInformation("User logged in with {Provider}", info.LoginProvider);
            return LocalRedirect(returnUrl);
        }

        if (result.IsLockedOut)
        {
            return RedirectToPage("/Account/Lockout");
        }

        // If the user does not have an account, create one
        var email = info.Principal.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
        var name = info.Principal.FindFirstValue(ClaimTypes.Name);
        var pictureUrl = info.Principal.FindFirstValue("urn:google:picture");

        if (string.IsNullOrEmpty(email))
        {
            _logger.LogWarning("Email not provided by external provider");
            return RedirectToAction(nameof(Login));
        }

        var user = new User
        {
            UserName = email,
            Email = email,
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

                // Auto-merge if a contact exists with the same email
                var existingContact = await _contactService.FindContactByEmailAsync(email);
                if (existingContact is not null)
                {
                    await _contactService.MergeContactToMemberAsync(
                        existingContact, user, null, "OAuth signup auto-merge");
                }

                await _signInManager.SignInAsync(user, isPersistent: false);
                _logger.LogInformation("User created an account using {Provider}", info.LoginProvider);
                return LocalRedirect(returnUrl);
            }
        }

        foreach (var error in createResult.Errors)
        {
            ModelState.AddModelError(string.Empty, error.Description);
        }

        return View(nameof(Login));
    }

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
}
