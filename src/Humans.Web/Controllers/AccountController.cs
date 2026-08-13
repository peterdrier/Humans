using Humans.UI.Controllers;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;
using Humans.Application.Extensions;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Users;
using Humans.UI;
using Humans.Web.Infrastructure;
using Humans.Users.Contracts;

namespace Humans.Web.Controllers;

public class AccountController(
    SignInManager<User> signInManager,
    IUserService userService,
    UserManager<User> userManager,
    IClock clock,
    ILogger<AccountController> logger,
    IExternalLoginService externalLoginService,
    IMagicLinkService magicLinkService,
    IAccountProvisioningService accountProvisioningService,
    GateLoginThrottle gateThrottle,
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

        var result = await signInManager.ExternalLoginSignInAsync(
            info.LoginProvider,
            info.ProviderKey,
            isPersistent: true,
            bypassTwoFactor: true);

        // Deliberately not passing HttpContext.RequestAborted: a client disconnect
        // mid-provisioning would abort past the rollback and strand a half-built
        // account. The callback runs to completion once Identity has answered.
        var completion = await externalLoginService.CompleteExternalLoginAsync(
            BuildExternalLoginAttempt(
                info,
                result.Succeeded,
                result.IsLockedOut,
                IsAuthenticated() ? GetCurrentUserId() : null));

        if (completion.SignInUser is not null)
            await signInManager.SignInAsync(completion.SignInUser, isPersistent: true);

        switch (completion.Outcome)
        {
            case ExternalLoginOutcome.SignedIn:
                return RedirectToLocal(returnUrl);

            case ExternalLoginOutcome.LinkToCurrentUserFailed:
                SetError(localizer["EmailGrid_LinkFailed"].Value);
                return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl : "/Profile/Me/Emails");

            case ExternalLoginOutcome.LockedOut:
                return RedirectToAction(nameof(Login), new { returnUrl, error = "lockedout" });

            case ExternalLoginOutcome.ProviderError:
                return RedirectToAction(nameof(Login), new { returnUrl, error = "oauth" });

            case ExternalLoginOutcome.CreateFailed:
                return LoginViewWithErrors(returnUrl, completion.Errors);

            case ExternalLoginOutcome.SetupFailed:
            default:
                return LoginViewWithModelError(returnUrl);
        }
    }

    // Projects the provider's assertion into the service's input. OIDC
    // email_verified missing/unparseable reads as false (the displacement gate
    // then refuses to displace).
    private static ExternalLoginAttempt BuildExternalLoginAttempt(
        ExternalLoginInfo info,
        bool providerSignInSucceeded,
        bool providerSignInLockedOut,
        Guid? currentUserId) =>
        new(
            Login: info,
            Email: info.Principal.FindFirstValue(ClaimTypes.Email) ?? string.Empty,
            DisplayName: info.Principal.FindFirstValue(ClaimTypes.Name),
            EmailVerified: bool.TryParse(info.Principal.FindFirstValue("email_verified"), out var verified) && verified,
            ProviderSignInSucceeded: providerSignInSucceeded,
            ProviderSignInLockedOut: providerSignInLockedOut,
            CurrentUserId: currentUserId);

    private IActionResult LoginViewWithErrors(string returnUrl, IEnumerable<string> errors)
    {
        foreach (var error in errors)
            ModelState.AddModelError(string.Empty, error);

        ViewData["ReturnUrl"] = returnUrl;
        return View(nameof(Login));
    }

    private IActionResult LoginViewWithModelError(string returnUrl)
    {
        ModelState.AddModelError(string.Empty,
            "We couldn't finish setting up your account. Please try again.");
        ViewData["ReturnUrl"] = returnUrl;
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
        ViewData["ExpiryTime"] = DateFormattingExtensions.TimeOfDayPattern.Format(expiryLocal.TimeOfDay);
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

        await userService.RecordLoginAsync(user.Id);

        await signInManager.SignInAsync(user, isPersistent: true);
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
    public async Task<IActionResult> CompleteSignup(
        string token, string burnerName, string firstName, string lastName,
        string? email = null, string? returnUrl = null)
    {
        if (string.IsNullOrEmpty(token))
            return View("MagicLinkError");

        returnUrl ??= Url.Content("~/");

        var verifiedEmail = magicLinkService.VerifySignupToken(token, email);
        if (verifiedEmail is null)
        {
            return View("MagicLinkError");
        }

        if (string.IsNullOrWhiteSpace(burnerName) ||
            string.IsNullOrWhiteSpace(firstName) ||
            string.IsNullOrWhiteSpace(lastName))
        {
            ViewData["ReturnUrl"] = returnUrl;
            ViewData["Email"] = verifiedEmail;
            ViewData["Token"] = token;
            ViewData["BurnerName"] = burnerName;
            ViewData["FirstName"] = firstName;
            ViewData["LastName"] = lastName;
            ModelState.AddModelError(string.Empty, localizer["CompleteSignup_AllFieldsRequired"]);
            return View("CompleteSignup");
        }

        var result = await accountProvisioningService.CompleteMagicLinkSignupAsync(
            verifiedEmail,
            burnerName,
            firstName,
            lastName,
            HttpContext.RequestAborted);

#pragma warning disable CS0618 // result.User is a record field on MagicLinkSignupCompletionResult, not a cross-domain nav read; arch test pattern-matches the literal `.User`.
        if (result.User is null)
            return View("MagicLinkError");

        await signInManager.SignInAsync(result.User, isPersistent: true);
#pragma warning restore CS0618

        return RedirectToLocal(returnUrl);
    }

    // --- Gate terminal ---

    /// <summary>
    /// Shared gate-terminal sign-in for the laptop at gate (see
    /// <see cref="SystemUserIds.GateTerminal"/>). Credential is set from the
    /// ticketing admin page; the session is persistent so the device survives
    /// restarts without an admin on-site.
    /// </summary>
    [HttpGet]
    public IActionResult GateLogin() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GateLogin(string? username, string? password)
    {
        // Throttle by source IP, never by account — anyone failing passwords on
        // purpose must only lock themselves out, not the terminal at gate.
        var source = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (gateThrottle.SecondsUntilRetry(source) is { } waitSeconds)
        {
            logger.LogWarning(
                "Gate terminal sign-in throttled for {Source} ({WaitSeconds}s remaining)",
                source, waitSeconds);
            ModelState.AddModelError(string.Empty, localizer["GateLogin_Throttled", waitSeconds]);
            return View();
        }

        var user = string.Equals(username?.Trim(), SystemUserIds.GateTerminalLoginName,
                StringComparison.OrdinalIgnoreCase)
            ? await userManager.FindByIdAsync(SystemUserIds.GateTerminal.ToString())
            : null;

        if (user is null || string.IsNullOrEmpty(password))
        {
            gateThrottle.RecordFailure(source);
            ModelState.AddModelError(string.Empty, localizer["GateLogin_Invalid"]);
            return View();
        }

        var result = await signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: false);
        if (!result.Succeeded)
        {
            gateThrottle.RecordFailure(source);
            logger.LogWarning("Gate terminal sign-in failed (wrong password) from {Source}", source);
            ModelState.AddModelError(string.Empty, localizer["GateLogin_Invalid"]);
            return View();
        }

        gateThrottle.Reset(source);

        await userService.RecordLoginAsync(user.Id);

        await signInManager.SignInAsync(user, isPersistent: true);
        logger.LogInformation("Gate terminal signed in");

        // Land the kiosk on the new gate terminal (which redirects to the claim screen
        // to pick who's scanning), not the old read-only Scanner section.
        // GateController is internal to Humans.Gate since its G5 move, so the action name is
        // a literal here rather than a nameof. Guarded by AdminNavTreeRoutingTests' sibling
        // route sweep — a rename on either side fails a routed-endpoint assertion, not the build.
        return RedirectToAction("Index", "Gate");
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
            ? LocalRedirect(returnUrl)
            : Redirect(Url.Content("~/"));
}
