using Humans.Base.Interfaces;
using Humans.Auth.Contracts;
using Humans.Base.Controllers;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;
using Humans.Base.Extensions;
using Humans.Base.Constants;
using Humans.Base;
using Humans.Web.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Controllers;

// Sign-in flows run before there is a principal.
[AllowAnonymous]
public class AccountController(
    IUserSignInService signIn,
    IUserService userService,
    IUserWrite userWrite,
    IClock clock,
    IOrgTimeZone orgTimeZone,
    ILogger<AccountController> logger,
    IExternalLoginService externalLoginService,
    ISignInLinkService signInLinkService,
    IAccountProvisioningService accountProvisioningService,
    GateSignInThrottle gateThrottle,
    SignInMethodCounter signInMethods,
    IStringLocalizer<SharedResource> localizer,
    IAuthenticationSchemeProvider authSchemes) : HumansControllerBase(userService)
{
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;

        // Link previews: chat crawlers land here anonymously, so the Open Graph tags describe
        // the area the shared link points at instead of "Sign In" — see SignInLinkPreview.
        var segment = SignInLinkPreview.SegmentFor(returnUrl);
        ViewData["OgTitle"] = segment is null
            ? localizer["Nav_Brand"].Value
            : $"{localizer[$"LinkPreview_{segment}_Title"].Value} - {localizer["Nav_Brand"].Value}";
        if (segment is not null)
        {
            ViewData["OgDescription"] = localizer[$"LinkPreview_{segment}_Description"].Value;
            ViewData["OgPath"] = returnUrl;
        }

        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExternalLogin(string provider, string? returnUrl = null)
    {
        if (await authSchemes.GetSchemeAsync(provider) is null)
            return RedirectToAction(nameof(Login), new { returnUrl, error = "oauth" });

        var redirectUrl = Url.Action(nameof(ExternalLoginCallback), "Account", new { returnUrl });
        var properties = signIn.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
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

        var info = await signIn.GetExternalLoginInfoAsync();
        if (info is null)
        {
            logger.LogWarning("Could not get external login info");
            return RedirectToAction(nameof(Login), new { returnUrl, error = "oauth" });
        }

        // Already signed in means this callback links a provider account, not a sign-in —
        // skip ExternalLoginSignInAsync so Identity cannot overwrite the session cookie with another account.
        var wasAuthenticated = IsAuthenticated();
        var result = wasAuthenticated
            ? Microsoft.AspNetCore.Identity.SignInResult.Failed
            : await signIn.ExternalLoginSignInAsync(info);

        // Deliberately not passing HttpContext.RequestAborted: a client disconnect
        // mid-provisioning would abort past the rollback and strand a half-built
        // account. The callback runs to completion once Identity has answered.
        var completion = await externalLoginService.CompleteExternalLoginAsync(
            BuildExternalLoginAttempt(
                info,
                result.Succeeded,
                result.IsLockedOut,
                wasAuthenticated ? GetCurrentUserId() : null));

        if (completion.SignInUserId is { } signInUserId)
            await signIn.SignInAsync(signInUserId);

        switch (completion.Outcome)
        {
            case ExternalLoginOutcome.SignedIn:
                if (!wasAuthenticated)
                    signInMethods.RecordProvider(info.LoginProvider);
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
            localizer["Login_SetupFailed"].Value);
        ViewData["ReturnUrl"] = returnUrl;
        return View(nameof(Login));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignInLinkRequest(string email, string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return RedirectToAction(nameof(Login), new { returnUrl });
        }

        try
        {
            await signInLinkService.SendSignInLinkAsync(email.Trim(), returnUrl);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending sign-in link for {Email}", email);
        }

        // Always show "check your email" — no account enumeration.
        var expiryInstant = clock.GetCurrentInstant() + Duration.FromMinutes(15);
        var expiryLocal = expiryInstant.InZone(orgTimeZone.Zone);
        ViewData["ExpiryTime"] = DateFormattingExtensions.TimeOfDayPattern.Format(expiryLocal.TimeOfDay);
        return View("SignInLinkSent");
    }

    [HttpGet]
    public IActionResult SignInLinkConfirm(Guid userId, string token, string? returnUrl = null)
    {
        // Landing page prevents email scanners from consuming the token; sign-in is POST.
        ViewData["UserId"] = userId;
        ViewData["Token"] = token;
        ViewData["ReturnUrl"] = returnUrl;
        return View("SignInLinkConfirm");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignInLink(Guid userId, string token, string? returnUrl = null)
    {
        returnUrl ??= Url.Content("~/");

        if (await signInLinkService.VerifySignInTokenAsync(userId, token) is { } failure)
        {
            return SignInLinkError(failure);
        }

        await userWrite.RecordSignInAsync(userId);

        if (!await signIn.SignInAsync(userId))
        {
            return SignInLinkError(SignInLinkFailure.AccountNotFound);
        }

        signInMethods.RecordSignInLink();
        logger.LogInformation("User {UserId} signed in via sign-in link", userId);

        return RedirectToLocal(returnUrl);
    }

    [HttpGet]
    public IActionResult SignInLinkSignup(string token, string? email = null, string? returnUrl = null)
    {
        if (string.IsNullOrEmpty(token))
            return SignInLinkError(SignInLinkFailure.Unreadable);

        var check = signInLinkService.VerifySignupToken(token, email);
        if (check.Email is not { } verifiedEmail)
        {
            return SignInLinkError(check.Failure);
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
            return SignInLinkError(SignInLinkFailure.Unreadable);

        returnUrl ??= Url.Content("~/");

        var check = signInLinkService.VerifySignupToken(token, email);
        if (check.Email is not { } verifiedEmail)
        {
            return SignInLinkError(check.Failure);
        }

        foreach (var (value, field, label) in new[]
        {
            (burnerName, nameof(burnerName), "CompleteSignup_BurnerNameLabel"),
            (firstName, nameof(firstName), "CompleteSignup_FirstNameLabel"),
            (lastName, nameof(lastName), "CompleteSignup_LastNameLabel")
        })
        {
            if (value?.Trim().Length > 256)
                ModelState.AddModelError(field, localizer["Validation_MaxLength", localizer[label].Value, 256]);
        }
        if (string.IsNullOrWhiteSpace(burnerName) ||
            string.IsNullOrWhiteSpace(firstName) ||
            string.IsNullOrWhiteSpace(lastName))
            ModelState.AddModelError(string.Empty, localizer["CompleteSignup_AllFieldsRequired"]);

        if (!ModelState.IsValid)
        {
            ViewData["ReturnUrl"] = returnUrl;
            ViewData["Email"] = verifiedEmail;
            ViewData["Token"] = token;
            ViewData["BurnerName"] = burnerName;
            ViewData["FirstName"] = firstName;
            ViewData["LastName"] = lastName;
            return View("CompleteSignup");
        }

        // Redeem only once the submission is complete — consuming before the
        // validation re-render above would burn the link on a blank field.
        var redemption = await signInLinkService.ConsumeSignupTokenAsync(token, email);
        if (redemption.Email is not { } redeemedEmail)
        {
            // Already redeemed. A resubmit that carries the cookie the first POST set
            // is the person who just signed up, so send them on. Everyone else errors:
            // a replay, and also the losing half of a double-click, whose request was
            // issued before that cookie existed. That half sees the error page, but the
            // account and session the winning half created stand — their next navigation
            // is signed in. Nothing derived from the request could tell those two apart,
            // since a replay carries the same body.
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToLocal(returnUrl);

            return SignInLinkError(redemption.Failure);
        }

        SignInLinkSignupResult result;
        try
        {
            result = await accountProvisioningService.CompleteSignInLinkSignupAsync(
                redeemedEmail,
                burnerName,
                firstName,
                lastName,
                HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            // The reservation must still be released: no account may have been created, and a
            // retry either completes signup or finds the account this request already created.
            signInLinkService.ReleaseSignupToken(token);
            logger.LogWarning("Sign-in link signup request was cancelled for {Email}; " +
                "the signup token reservation was released", redeemedEmail);
            throw;
        }
        catch (Exception ex)
        {
            // Provisioning failed. Hand the link back before the exception surfaces: if it
            // created nothing, the retry signs them up; if it got as far as the account, the
            // retry finds it and signs them in.
            signInLinkService.ReleaseSignupToken(token);
            logger.LogError(ex, "Sign-in link signup: provisioning threw for {Email}; " +
                "the signup token reservation was released", redeemedEmail);
            throw;
        }

        if (await SignInNewAccountAsync(result.UserId) is { } signInError)
        {
            // The redemption accomplished nothing. Hand the link back rather than
            // making the person request a new email over a transient failure.
            signInLinkService.ReleaseSignupToken(token);
            return View("SignInLinkError", signInError);
        }

        signInMethods.RecordSignInLink();

        return RedirectToLocal(returnUrl);
    }

    /// <summary>Signs in the account signup just provisioned; returns the error page's reason when that fails.</summary>
    private async Task<string?> SignInNewAccountAsync(Guid? userId)
    {
        // A null id means provisioning failed and rolled itself back.
        if (userId is not { } id)
            return "signup-failed";
        return await signIn.SignInAsync(id) ? null : "account-not-found";
    }

    private ViewResult SignInLinkError(SignInLinkFailure? failure) => View("SignInLinkError", failure switch
    {
        SignInLinkFailure.Expired => "expired",
        SignInLinkFailure.AlreadyUsed => "used",
        SignInLinkFailure.AccountNotFound => "account-not-found",
        _ => "unreadable",
    });

    /// <summary>
    /// Shared gate-terminal sign-in for the laptop at gate (see
    /// <see cref="SystemUserIds.GateTerminal"/>). Credential is set from the
    /// ticketing admin page; the session is persistent so the device survives
    /// restarts without an admin on-site.
    /// </summary>
    [HttpGet]
    public IActionResult GateSignIn() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GateSignIn(string? username, string? password)
    {
        // Throttle by source IP, never by account — anyone failing passwords on
        // purpose must only lock themselves out, not the terminal at gate.
        var source = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (gateThrottle.SecondsUntilRetry(source) is { } waitSeconds)
        {
            logger.LogWarning(
                "Gate terminal sign-in throttled for {Source} ({WaitSeconds}s remaining)",
                source, waitSeconds);
            ModelState.AddModelError(string.Empty, localizer["GateSignIn_Throttled", waitSeconds]);
            return View();
        }

        var isGateUser = string.Equals(username?.Trim(), SystemUserIds.GateTerminalLoginName,
            StringComparison.OrdinalIgnoreCase);

        if (!isGateUser || string.IsNullOrEmpty(password))
        {
            gateThrottle.RecordFailure(source);
            ModelState.AddModelError(string.Empty,
                localizer[isGateUser ? "GateSignIn_NoPassword" : "GateSignIn_WrongUsername"]);
            return View();
        }

        if (!await signIn.CheckPasswordAsync(SystemUserIds.GateTerminal, password))
        {
            gateThrottle.RecordFailure(source);
            logger.LogWarning("Gate terminal sign-in failed (wrong password) from {Source}", source);
            ModelState.AddModelError(string.Empty, localizer["GateSignIn_WrongPassword"]);
            return View();
        }

        gateThrottle.Reset(source);

        await userWrite.RecordSignInAsync(SystemUserIds.GateTerminal);

        await signIn.SignInAsync(SystemUserIds.GateTerminal);
        logger.LogInformation("Gate terminal signed in");

        // GateController is internal to Humans.Gate, so the action is a literal. AdminNavTreeRoutingTests' sibling
        // route sweep catches a rename on either side.
        return RedirectToAction("Index", "Gate");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
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
