using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NodaTime;
using Humans.Domain.Entities;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Web.Controllers;

public class AccountController : HumansControllerBase
{
    private readonly SignInManager<User> _signInManager;
    private readonly UserManager<User> _userManager;
    private readonly IClock _clock;
    private readonly ILogger<AccountController> _logger;
    private readonly IUserEmailService _userEmailService;
    private readonly IMagicLinkService _magicLinkService;
    private readonly IProfileService _profileService;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public AccountController(
        SignInManager<User> signInManager,
        UserManager<User> userManager,
        IClock clock,
        ILogger<AccountController> logger,
        IUserEmailService userEmailService,
        IMagicLinkService magicLinkService,
        IProfileService profileService,
        IStringLocalizer<SharedResource> localizer)
        : base(userManager)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _clock = clock;
        _logger = logger;
        _userEmailService = userEmailService;
        _magicLinkService = magicLinkService;
        _profileService = profileService;
        _localizer = localizer;
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

                await TryReconcileOAuthIdentityAsync(existingUser.Id, info);
            }

            _logger.LogInformation("User logged in with {Provider}", info.LoginProvider);
            return RedirectToLocal(returnUrl);
        }

        // No existing login — try to link to an existing account by email
        var email = info.Principal.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
        var name = info.Principal.FindFirstValue(ClaimTypes.Name);
        var pictureUrl = info.Principal.FindFirstValue("urn:google:picture");

        // Link-while-signed-in branch: the user is already authenticated
        // (e.g. via magic link) and just clicked "Link Google account" on
        // /Profile/Me/Emails. Attach the OAuth identity to the current user
        // rather than searching by email or creating a new user. This must
        // run before the lockout, email-match, and create-new-user branches
        // so a fresh OAuth email never spawns a duplicate account.
        if (User.Identity?.IsAuthenticated == true)
        {
            var currentUser = await GetCurrentUserAsync();
            if (currentUser is not null)
            {
                var addLinkResult = await _userManager.AddLoginAsync(currentUser, info);
                if (addLinkResult.Succeeded)
                {
                    currentUser.LastLoginAt = _clock.GetCurrentInstant();
                    if (string.IsNullOrEmpty(currentUser.ProfilePictureUrl) && pictureUrl is not null)
                    {
                        currentUser.ProfilePictureUrl = pictureUrl;
                    }
                    await _userManager.UpdateAsync(currentUser);

                    if (!string.IsNullOrEmpty(email))
                    {
                        await TryReconcileOAuthIdentityAsync(currentUser.Id, info);
                    }

                    _logger.LogInformation(
                        "Linked {Provider} login to currently-authenticated user {UserId}",
                        info.LoginProvider, currentUser.Id);
                    return RedirectToLocal(returnUrl);
                }

                _logger.LogWarning(
                    "Failed to link {Provider} to authenticated user {UserId}: {Errors}",
                    info.LoginProvider, currentUser.Id,
                    string.Join(", ", addLinkResult.Errors.Select(e => e.Description)));

                // AddLoginAsync failed for an already-authenticated user. Do NOT
                // fall through to the lockedout / email-match / create-new-user
                // branches below — those exist for the unauthenticated flow and
                // can produce a duplicate User row when the caller is already
                // signed in. Surface the failure as an error toast on the emails
                // page instead.
                SetError(_localizer["EmailGrid_LinkFailed"].Value);
                return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/Profile/Me/Emails");
            }
        }

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

                                await TryReconcileOAuthIdentityAsync(activeTarget.Id, info);

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

                    await TryReconcileOAuthIdentityAsync(existingByEmail.Id, info);

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

        // Create the OAuth-linked UserEmail row via the reconcile entry point.
        // For a freshly created user this hits the NewRowCreated branch (no
        // existing rows, no cross-user collision unless the address is already
        // verified by another user). On reconcile throw OR a CrossUserBlocked
        // outcome (another user verified-holds the address and the provider's
        // email_verified claim is false — so reconcile refused to displace
        // and did NOT create a UserEmail row), roll back the User to avoid an
        // orphan that's unreachable via either OAuth or magic link. Symmetric
        // to CompleteSignup's cleanup.
        try
        {
            var reconcile = await _userEmailService.ReconcileOAuthIdentityAsync(
                user.Id,
                info.LoginProvider,
                info.ProviderKey,
                email,
                claimEmailVerified: ReadEmailVerifiedClaim(info));

            if (reconcile.Outcome == ReconcileOutcome.CrossUserBlocked)
            {
                throw new InvalidOperationException(
                    $"OAuth signup blocked: {email} is verified by user " +
                    $"{reconcile.DisplacedUserId} and the provider's email_verified " +
                    "claim is false.");
            }
        }
        catch (DbUpdateException dbex)
            when (dbex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            _logger.LogError(dbex,
                "OAuth signup race (Postgres 23505) on UserEmail unique index " +
                "for new user {UserId} (provider={Provider}, sub={Sub}, " +
                "claimEmail={Email}); rolling back user + login. The partial " +
                "unique index caught a concurrent insert past the reconcile " +
                "pre-check — investigate via /Profile/Admin/EmailProblems.",
                user.Id, info.LoginProvider, info.ProviderKey, email);
            await TryDeleteOrphanUserAsync(user);
            ModelState.AddModelError(string.Empty,
                "We couldn't finish setting up your account. Please try again.");
            ViewData["ReturnUrl"] = returnUrl;
            return View(nameof(Login));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to reconcile OAuth identity for new user {UserId} ({Email}); rolling back user + login",
                user.Id, email);
            await TryDeleteOrphanUserAsync(user);
            ModelState.AddModelError(string.Empty,
                "We couldn't finish setting up your account. Please try again.");
            ViewData["ReturnUrl"] = returnUrl;
            return View(nameof(Login));
        }

        // Issue #635 (§15i): Stub Profile invariant — every User has a Profile.
        await _profileService.EnsureStubProfileAsync(user.Id);

        await _signInManager.SignInAsync(user, isPersistent: false);
        _logger.LogInformation("User created an account using {Provider}", info.LoginProvider);
        return RedirectToLocal(returnUrl);
    }

    /// <summary>
    /// Calls <see cref="IUserEmailService.ReconcileOAuthIdentityAsync"/> for
    /// every OAuth-success path (existing-user sign-in, already-authenticated
    /// link, lockout-relink, email-match link). Sign-in must never block on
    /// reconcile failures — every exception is logged and swallowed. The
    /// service owns every audit row for the OAuth path; this controller writes
    /// none. The new-user creation path uses the reconcile result to decide
    /// whether to roll back, so it calls reconcile inline rather than via
    /// this helper.
    /// </summary>
    private async Task TryReconcileOAuthIdentityAsync(Guid userId, Microsoft.AspNetCore.Identity.ExternalLoginInfo info)
    {
        var claimEmail = info.Principal.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrEmpty(claimEmail))
            return;

        try
        {
            await _userEmailService.ReconcileOAuthIdentityAsync(
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
        catch (DbUpdateException dbex)
            when (dbex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // The verified-email partial unique index caught a concurrent
            // insert that beat the reconcile pre-check. Rare race; surface a
            // structured log so admins can investigate via the
            // EmailProblems scanner. Sign-in continues (never blocks).
            _logger.LogError(dbex,
                "OAuth reconcile race (Postgres 23505) for user {UserId} " +
                "(provider={Provider}, sub={Sub}, claimEmail={Email}); " +
                "sign-in continues — investigate via /Profile/Admin/EmailProblems.",
                userId, info.LoginProvider, info.ProviderKey, claimEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "OAuth reconcile failed for user {UserId} {Provider} sub={Sub}; sign-in continues",
                userId, info.LoginProvider, info.ProviderKey);
        }
    }

    /// <summary>
    /// Best-effort rollback of a freshly-created user when the OAuth signup
    /// path fails after <see cref="UserManager{TUser}.CreateAsync"/> +
    /// <see cref="UserManager{TUser}.AddLoginAsync"/> succeeded. A failure
    /// here leaves an orphan that's unreachable via either OAuth or magic
    /// link — logged at Error so admins can clean up manually.
    /// </summary>
    private async Task TryDeleteOrphanUserAsync(User user)
    {
        try
        {
            await _userManager.DeleteAsync(user);
        }
        catch (Exception deleteEx)
        {
            _logger.LogError(deleteEx,
                "Failed to clean up orphan user {UserId} after reconcile failure",
                user.Id);
        }
    }

    /// <summary>
    /// Reads the OIDC <c>email_verified</c> boolean from the principal. Google
    /// surfaces it via the <c>email_verified</c> claim (mapped in
    /// <c>Program.cs</c>). Returns <c>false</c> when the claim is missing or
    /// unparseable — the displacement gate in the service treats this as
    /// "don't displace another user".
    /// </summary>
    private static bool ReadEmailVerifiedClaim(Microsoft.AspNetCore.Identity.ExternalLoginInfo info)
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

        // RequireUniqueEmail = false: Identity no longer rejects partial state,
        // so if creating the UserEmail row fails we must roll back the User to
        // avoid an orphan that's unreachable via either OAuth or magic link.
        try
        {
            await _userEmailService.AddVerifiedEmailAsync(user.Id, email);
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
                    "Failed to clean up orphan user {UserId} after AddVerifiedEmailAsync failure",
                    user.Id);
            }
            return View("MagicLinkError");
        }

        // Issue #635 (§15i): Stub Profile invariant — every User has a Profile.
        await _profileService.EnsureStubProfileAsync(user.Id);

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
