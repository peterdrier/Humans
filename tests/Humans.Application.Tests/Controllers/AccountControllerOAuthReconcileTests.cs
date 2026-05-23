using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Controllers;

/// <summary>
/// Issue nobodies-collective/Humans#697: the OAuth callback drives every
/// <see cref="UserEmail"/> mutation through
/// <see cref="IUserEmailService.ReconcileOAuthIdentityAsync"/>. The
/// controller writes no audit rows on the OAuth path — audit ownership
/// moved into the service. These tests pin the 5 OAuth-success paths
/// listed in the issue's acceptance criteria.
/// </summary>
public class AccountControllerOAuthReconcileTests
{
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IMagicLinkService _magicLinkService = Substitute.For<IMagicLinkService>();
    private readonly IStringLocalizer<Web.SharedResource> _localizer =
        Substitute.For<IStringLocalizer<Web.SharedResource>>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 5, 11, 12, 0));
    private readonly UserManager<User> _userManager;
    private readonly SignInManager<User> _signInManager;
    private readonly AccountController _controller;

    private const string Provider = "Google";
    private const string ProviderKey = "google-sub-12345";

    public AccountControllerOAuthReconcileTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);

        var contextAccessor = Substitute.For<IHttpContextAccessor>();
        var claimsFactory = Substitute.For<IUserClaimsPrincipalFactory<User>>();
        var identityOptions = Substitute.For<IOptions<IdentityOptions>>();
        identityOptions.Value.Returns(new IdentityOptions());
        var schemeProvider = Substitute.For<IAuthenticationSchemeProvider>();
        var userConfirmation = Substitute.For<IUserConfirmation<User>>();

        _signInManager = Substitute.For<SignInManager<User>>(
            _userManager, contextAccessor, claimsFactory, identityOptions,
            NullLogger<SignInManager<User>>.Instance, schemeProvider, userConfirmation);

        _localizer[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        _controller = new AccountController(
            _signInManager,
            Substitute.For<IUserService>(),
            _userManager,
            _clock,
            NullLogger<AccountController>.Instance,
            _userEmailService,
            _magicLinkService,
            Substitute.For<IAccountProvisioningService>(),
            _localizer);
        _controller.Url = Substitute.For<IUrlHelper>();
        _controller.Url.IsLocalUrl(Arg.Any<string?>()).Returns(false);
        _controller.Url.Content(Arg.Any<string>()).Returns(ci => ci.Arg<string>());

        var tempDataProvider = Substitute.For<ITempDataProvider>();
        var tempDataDictionaryFactory = new TempDataDictionaryFactory(tempDataProvider);
        _controller.TempData = tempDataDictionaryFactory.GetTempData(
            new DefaultHttpContext());

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        // Default reconcile result — tests override per-scenario when they
        // need the result to drive a controller branch.
        _userEmailService.ReconcileOAuthIdentityAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new OAuthReconcileResult(
                ReconcileOutcome.NoChange, null, Guid.NewGuid(),
                null, null, null, false));
    }

    private static ExternalLoginInfo MakeInfo(string email, bool emailVerified, string name = "Test User")
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Name, name),
            new(ClaimTypes.NameIdentifier, ProviderKey),
        };
        if (emailVerified)
            claims.Add(new Claim("email_verified", "true"));
        else
            claims.Add(new Claim("email_verified", "false"));

        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        return new ExternalLoginInfo(principal, Provider, ProviderKey, "Google");
    }

    // ─── Path 1: existing-user sign-in success ───────────────────────────────

    [HumansFact]
    public async Task ExistingUserSignInSuccess_CallsReconcileOnceWithVerifiedClaim()
    {
        var userId = Guid.NewGuid();
        var info = MakeInfo("user@example.com", emailVerified: true);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, false, true)
            .Returns(SignInResult.Success);

        var existingUser = new User { Id = userId };
        _userManager.FindByLoginAsync(Provider, ProviderKey).Returns(existingUser);
        _userManager.UpdateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);

        await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        await _userEmailService.Received(1).ReconcileOAuthIdentityAsync(
            userId, Provider, ProviderKey, "user@example.com",
            claimEmailVerified: true, Arg.Any<CancellationToken>());

        // Controller writes no audit on the OAuth path — audit is service-owned.
        // (The controller no longer takes an IAuditLogService dependency,
        // so it is structurally impossible to write one from this branch.)
    }

    [HumansFact]
    public async Task ExistingUserSignInSuccess_ReconcileThrows_SignInStillSucceeds()
    {
        var userId = Guid.NewGuid();
        var info = MakeInfo("user@example.com", emailVerified: true);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, false, true)
            .Returns(SignInResult.Success);

        var existingUser = new User { Id = userId };
        _userManager.FindByLoginAsync(Provider, ProviderKey).Returns(existingUser);
        _userManager.UpdateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);

        _userEmailService.ReconcileOAuthIdentityAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<OAuthReconcileResult>(_ => throw new InvalidOperationException("boom"));

        // The action must complete without throwing — sign-in NEVER blocks.
        var result = await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);
        result.Should().NotBeNull();
    }

    // ─── Path 2: already-authenticated link ──────────────────────────────────

    [HumansFact]
    public async Task AlreadyAuthenticatedLink_CallsReconcileForCurrentUser()
    {
        var currentUserId = Guid.NewGuid();
        var newEmail = "secondary@google.test";
        var info = MakeInfo(newEmail, emailVerified: true);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, false, true)
            .Returns(SignInResult.Failed);

        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, currentUserId.ToString())
        ], authenticationType: "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        var currentUser = new User { Id = currentUserId };
        _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(currentUser);
        _userManager.AddLoginAsync(currentUser, Arg.Any<UserLoginInfo>())
            .Returns(IdentityResult.Success);
        _userManager.UpdateAsync(currentUser).Returns(IdentityResult.Success);

        await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        await _userManager.DidNotReceive().CreateAsync(Arg.Any<User>());
        await _userEmailService.Received(1).ReconcileOAuthIdentityAsync(
            currentUserId, Provider, ProviderKey, newEmail,
            claimEmailVerified: true, Arg.Any<CancellationToken>());
    }

    // ─── Path 3: lockout-relink ──────────────────────────────────────────────

    [HumansFact]
    public async Task LockoutRelink_CallsReconcileForActiveTarget()
    {
        var lockedSourceId = Guid.NewGuid();
        var activeTargetId = Guid.NewGuid();
        var email = "relink@example.com";
        var info = MakeInfo(email, emailVerified: true);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, false, true)
            .Returns(SignInResult.LockedOut);

        var lockedSource = new User { Id = lockedSourceId };
        var activeTarget = new User { Id = activeTargetId };
        _userManager.FindByLoginAsync(Provider, ProviderKey).Returns(lockedSource);
        _magicLinkService.FindUserByVerifiedEmailAsync(email, Arg.Any<CancellationToken>())
            .Returns(activeTarget);
        _userManager.RemoveLoginAsync(lockedSource, Provider, ProviderKey)
            .Returns(IdentityResult.Success);
        _userManager.AddLoginAsync(activeTarget, Arg.Any<UserLoginInfo>())
            .Returns(IdentityResult.Success);
        _userManager.UpdateAsync(activeTarget).Returns(IdentityResult.Success);

        await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        await _userEmailService.Received(1).ReconcileOAuthIdentityAsync(
            activeTargetId, Provider, ProviderKey, email,
            claimEmailVerified: true, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task LockoutRelink_UnverifiedEmailClaim_PassedThroughAsFalse()
    {
        // Pre-697 the lockout-relink branch had NO UserEmail sync at all;
        // this is the highest-risk plumbing path. Pin the email_verified=false
        // case alongside the existing verified case so the claim flows through
        // every OAuth-success path.
        var lockedSourceId = Guid.NewGuid();
        var activeTargetId = Guid.NewGuid();
        var email = "relink@example.com";
        var info = MakeInfo(email, emailVerified: false);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, false, true)
            .Returns(SignInResult.LockedOut);

        var lockedSource = new User { Id = lockedSourceId };
        var activeTarget = new User { Id = activeTargetId };
        _userManager.FindByLoginAsync(Provider, ProviderKey).Returns(lockedSource);
        _magicLinkService.FindUserByVerifiedEmailAsync(email, Arg.Any<CancellationToken>())
            .Returns(activeTarget);
        _userManager.RemoveLoginAsync(lockedSource, Provider, ProviderKey)
            .Returns(IdentityResult.Success);
        _userManager.AddLoginAsync(activeTarget, Arg.Any<UserLoginInfo>())
            .Returns(IdentityResult.Success);
        _userManager.UpdateAsync(activeTarget).Returns(IdentityResult.Success);

        await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        await _userEmailService.Received(1).ReconcileOAuthIdentityAsync(
            activeTargetId, Provider, ProviderKey, email,
            claimEmailVerified: false, Arg.Any<CancellationToken>());
    }

    // ─── Path 4: email-match link (unauthenticated, address already known) ───

    [HumansFact]
    public async Task EmailMatchLink_CallsReconcileForMatchedUser()
    {
        var existingUserId = Guid.NewGuid();
        var email = "linked@example.com";
        var info = MakeInfo(email, emailVerified: true);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, false, true)
            .Returns(SignInResult.Failed);

        var existingUser = new User { Id = existingUserId };
        _magicLinkService.FindUserByVerifiedEmailAsync(email, Arg.Any<CancellationToken>())
            .Returns(existingUser);

        _userManager.AddLoginAsync(existingUser, Arg.Any<UserLoginInfo>())
            .Returns(IdentityResult.Success);
        _userManager.UpdateAsync(existingUser).Returns(IdentityResult.Success);

        await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        await _userEmailService.Received(1).ReconcileOAuthIdentityAsync(
            existingUserId, Provider, ProviderKey, email,
            claimEmailVerified: true, Arg.Any<CancellationToken>());
    }

    // ─── Path 5: new-user creation ───────────────────────────────────────────

    [HumansFact]
    public async Task NewUserCreation_CallsReconcileForCreatedUserWithVerifiedClaim()
    {
        var newEmail = "fresh@example.com";
        var info = MakeInfo(newEmail, emailVerified: true);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, false, true)
            .Returns(SignInResult.Failed);
        _magicLinkService.FindUserByVerifiedEmailAsync(newEmail, Arg.Any<CancellationToken>())
            .Returns((User?)null);

        _userManager.CreateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);
        _userManager.AddLoginAsync(Arg.Any<User>(), Arg.Any<UserLoginInfo>())
            .Returns(IdentityResult.Success);

        Guid createdUserId = Guid.Empty;
        _userEmailService.ReconcileOAuthIdentityAsync(
            Arg.Do<Guid>(id => createdUserId = id),
            Provider, ProviderKey, newEmail,
            claimEmailVerified: true, Arg.Any<CancellationToken>())
            .Returns(new OAuthReconcileResult(
                ReconcileOutcome.NewRowCreated, null, Guid.NewGuid(),
                null, null, null, false));

        await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        await _userEmailService.Received(1).ReconcileOAuthIdentityAsync(
            Arg.Is<Guid>(id => id == createdUserId),
            Provider, ProviderKey, newEmail,
            claimEmailVerified: true, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task NewUserCreation_ReconcileThrows_RollsBackUser()
    {
        var newEmail = "fresh@example.com";
        var info = MakeInfo(newEmail, emailVerified: true);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, false, true)
            .Returns(SignInResult.Failed);
        _magicLinkService.FindUserByVerifiedEmailAsync(newEmail, Arg.Any<CancellationToken>())
            .Returns((User?)null);

        _userManager.CreateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);
        _userManager.AddLoginAsync(Arg.Any<User>(), Arg.Any<UserLoginInfo>())
            .Returns(IdentityResult.Success);
        _userManager.DeleteAsync(Arg.Any<User>()).Returns(IdentityResult.Success);

        _userEmailService.ReconcileOAuthIdentityAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<OAuthReconcileResult>(_ => throw new InvalidOperationException("boom"));

        await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        // Failed reconcile rolls back: DeleteAsync called for the newly-created user.
        await _userManager.Received(1).DeleteAsync(Arg.Any<User>());
    }

    [HumansFact]
    public async Task NewUserCreation_CrossUserBlocked_RollsBackUser()
    {
        // Distinct code path from ReconcileThrows: reconcile returns
        // normally with Outcome=CrossUserBlocked (provider's email_verified
        // claim was false while another user verified-holds the address).
        // The controller must still roll back the newly-created User +
        // AspNetUserLogins — otherwise an orphan is left in the database
        // un-notifiable + magic-link-unreachable.
        var newEmail = "blocked@example.com";
        var info = MakeInfo(newEmail, emailVerified: false);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, false, true)
            .Returns(SignInResult.Failed);
        _magicLinkService.FindUserByVerifiedEmailAsync(newEmail, Arg.Any<CancellationToken>())
            .Returns((User?)null);
        _userManager.CreateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);
        _userManager.AddLoginAsync(Arg.Any<User>(), Arg.Any<UserLoginInfo>())
            .Returns(IdentityResult.Success);
        _userManager.DeleteAsync(Arg.Any<User>()).Returns(IdentityResult.Success);

        _userEmailService.ReconcileOAuthIdentityAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new OAuthReconcileResult(
                ReconcileOutcome.CrossUserBlocked,
                PreviousEmail: null,
                AffectedRowId: null,
                DisplacedUserId: Guid.NewGuid(),
                DisplacedRowId: Guid.NewGuid(),
                DisplacedEmail: newEmail,
                DisplacedUserLeftWithoutVerifiedEmail: false));

        await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        await _userManager.Received(1).DeleteAsync(Arg.Any<User>());
    }

    // ─── email_verified claim plumbing ───────────────────────────────────────

    [HumansFact]
    public async Task ExistingUserSignInSuccess_UnverifiedEmailClaim_PassedThroughAsFalse()
    {
        var userId = Guid.NewGuid();
        var info = MakeInfo("user@example.com", emailVerified: false);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, false, true)
            .Returns(SignInResult.Success);

        var existingUser = new User { Id = userId };
        _userManager.FindByLoginAsync(Provider, ProviderKey).Returns(existingUser);
        _userManager.UpdateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);

        await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        await _userEmailService.Received(1).ReconcileOAuthIdentityAsync(
            userId, Provider, ProviderKey, "user@example.com",
            claimEmailVerified: false, Arg.Any<CancellationToken>());
    }
}
