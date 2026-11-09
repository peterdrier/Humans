using Humans.Users.Services;
using Humans.Auth.Contracts;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Users.Contracts;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Services.Users;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// The OAuth callback drives every <see cref="UserEmail"/> mutation through
/// <see cref="IUserEmailService.ReconcileOAuthIdentityAsync"/>, which owns the audit. Driven through a real
/// <see cref="ExternalLoginService"/>, so the whole callback is covered.
/// </summary>
public class AccountControllerOAuthReconcileTests
{
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IMagicLinkService _magicLinkService = Substitute.For<IMagicLinkService>();
    private readonly IUserManagement _userService = Substitute.For<IUserManagement>();
    private readonly IStringLocalizer<Base.SharedResource> _localizer =
        Substitute.For<IStringLocalizer<Base.SharedResource>>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 5, 11, 12, 0));
    private readonly Web.Services.LoginMethodCounter _loginMethods = new();
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

        var externalLoginService = new ExternalLoginService(
            _userManager,
            _userService,
            _userEmailService,
            _clock,
            NullLogger<ExternalLoginService>.Instance);

        _controller = new AccountController(
            new UserSignInService(_signInManager, _userManager, Substitute.For<IServiceScopeFactory>()),
            _userService,
            _userService,
            _clock,
            NullLogger<AccountController>.Instance,
            externalLoginService,
            _magicLinkService,
            Substitute.For<IAccountProvisioningService>(),
            new Web.Services.GateLoginThrottle(
                new Microsoft.Extensions.Caching.Memory.MemoryCache(
                    new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
                _clock),
            _loginMethods,
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

    [HumansFact]
    public async Task ExistingUserSignInSuccess_CallsReconcileOnceWithVerifiedClaim()
    {
        var userId = Guid.NewGuid();
        var info = MakeInfo("user@example.com", emailVerified: true);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, true, true)
            .Returns(SignInResult.Success);

        var existingUser = new User { Id = userId };
        _userManager.FindByLoginAsync(Provider, ProviderKey).Returns(existingUser);
        _userManager.UpdateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);

        await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        await _userEmailService.Received(1).ReconcileOAuthIdentityAsync(
            userId, Provider, ProviderKey, "user@example.com",
            claimEmailVerified: true, Arg.Any<CancellationToken>());
        await _userService.Received(1).EnsureStubProfileAsync(userId, null, null, null,
            Arg.Any<CancellationToken>(), userId);

        _loginMethods.Google.Should().Be(1);
        _loginMethods.MagicLink.Should().Be(0);
    }

    [HumansFact]
    public async Task OAuthProfileFailure_DoesNotBlockNewAccount_AndKnownLoginRetriesRepair()
    {
        var email = "fresh@example.com";
        _signInManager.GetExternalLoginInfoAsync().Returns(MakeInfo(email, emailVerified: true));
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, true, true).Returns(SignInResult.Failed);
        StubVerifiedEmailOwner(email, null);
        User? created = null;
        _userManager.CreateAsync(Arg.Do<User>(user => created = user)).Returns(IdentityResult.Success);
        _userManager.FindByIdAsync(Arg.Any<string>()).Returns(_ => created);
        _userManager.AddLoginAsync(Arg.Any<User>(), Arg.Any<UserLoginInfo>()).Returns(IdentityResult.Success);
        var profileAttempts = 0;
        _userService.EnsureStubProfileAsync(Arg.Any<Guid>(), null, null, null,
                Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(_ => ++profileAttempts == 1 ? throw new IOException("profile store unavailable") : true);

        var first = await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        first.Should().BeOfType<RedirectResult>();
        created.Should().NotBeNull();
        await _signInManager.Received(1).SignInAsync(created!, true);
        await _userManager.DidNotReceive().DeleteAsync(Arg.Any<User>());
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, true, true).Returns(SignInResult.Success);
        _userManager.FindByLoginAsync(Provider, ProviderKey).Returns(created);

        var retry = await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        retry.Should().BeOfType<RedirectResult>();
        profileAttempts.Should().Be(2);
        await _userService.Received(2).EnsureStubProfileAsync(created!.Id, null, null, null,
            Arg.Any<CancellationToken>(), created.Id);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ExistingUserSignInSuccess_RepairFailure_SignInStillSucceeds(bool profileFailure)
    {
        var userId = Guid.NewGuid();
        var info = MakeInfo("user@example.com", emailVerified: true);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, true, true)
            .Returns(SignInResult.Success);

        var existingUser = new User { Id = userId };
        _userManager.FindByLoginAsync(Provider, ProviderKey).Returns(existingUser);
        _userManager.UpdateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);

        if (profileFailure)
            _userService.EnsureStubProfileAsync(userId, null, null, null,
                    Arg.Any<CancellationToken>(), userId)
                .Returns<bool>(_ => throw new IOException("profile store unavailable"));
        else
            _userEmailService.ReconcileOAuthIdentityAsync(
                    Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns<OAuthReconcileResult>(_ => throw new InvalidOperationException("boom"));

        // Sign-in never blocks.
        var result = await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);
        result.Should().BeOfType<RedirectResult>();
        await _userEmailService.Received(1).ReconcileOAuthIdentityAsync(
            userId, Provider, ProviderKey, "user@example.com", true, Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task OAuthLink_ProfileRepairCancellation_DoesNotFallThroughToAccountCreation(bool lockedOut)
    {
        var target = new User { Id = Guid.NewGuid() };
        var info = MakeInfo("linked@example.com", emailVerified: true);
        StubVerifiedEmailOwner("linked@example.com", target);
        _userManager.FindByLoginAsync(Provider, ProviderKey).Returns(new User { Id = Guid.NewGuid() });
        _userManager.RemoveLoginAsync(Arg.Any<User>(), Provider, ProviderKey).Returns(IdentityResult.Success);
        _userManager.AddLoginAsync(target, Arg.Any<UserLoginInfo>()).Returns(IdentityResult.Success);
        using var cancelled = new CancellationTokenSource();
        _userService.EnsureStubProfileAsync(target.Id, null, null, null, cancelled.Token, target.Id)
            .Returns<bool>(_ =>
            {
                cancelled.Cancel();
                throw new OperationCanceledException(cancelled.Token);
            });
        var service = new ExternalLoginService(_userManager, _userService, _userEmailService, _clock,
            NullLogger<ExternalLoginService>.Instance);

        var act = () => service.CompleteExternalLoginAsync(
            new ExternalLoginAttempt(info, "linked@example.com", "Linked human", true,
                ProviderSignInSucceeded: false, ProviderSignInLockedOut: lockedOut, CurrentUserId: null), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await _userManager.DidNotReceive().CreateAsync(Arg.Any<User>());
    }

    [HumansFact]
    public async Task AlreadyAuthenticatedLink_CallsReconcileForCurrentUser()
    {
        var currentUserId = Guid.NewGuid();
        var newEmail = "secondary@google.test";
        var info = MakeInfo(newEmail, emailVerified: true);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, true, true)
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
        // The controller resolves the user from the NameIdentifier claim, not the principal.
        _userManager.FindByIdAsync(currentUserId.ToString()).Returns(currentUser);
        _userManager.AddLoginAsync(currentUser, Arg.Any<UserLoginInfo>())
            .Returns(IdentityResult.Success);
        _userManager.UpdateAsync(currentUser).Returns(IdentityResult.Success);

        await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        await _userManager.DidNotReceive().CreateAsync(Arg.Any<User>());
        await _userEmailService.Received(1).ReconcileOAuthIdentityAsync(
            currentUserId, Provider, ProviderKey, newEmail,
            claimEmailVerified: true, Arg.Any<CancellationToken>());
        await _userService.Received(1).EnsureStubProfileAsync(currentUserId, null, null, null,
            Arg.Any<CancellationToken>(), currentUserId);

        // Already-authenticated link keeps the existing session — no re-issue.
        await _signInManager.DidNotReceive().SignInAsync(
            Arg.Any<User>(), Arg.Any<bool>(), Arg.Any<string?>());

        // ...and linking is not a sign-in, so the dashboard tally doesn't move.
        _loginMethods.Google.Should().Be(0);
    }

    [HumansTheory]
    [Xunit.InlineData("session", UserState.Deleted)]
    [Xunit.InlineData("email", UserState.Merged)]
    [Xunit.InlineData("relink", UserState.Deleted)]
    public async Task OAuthLink_TerminalTarget_DoesNotRecreateCredentials(string path, UserState state)
    {
        const string email = "old@example.com";
        var target = new User { Id = Guid.NewGuid(), State = state };
        _signInManager.GetExternalLoginInfoAsync().Returns(MakeInfo(email, emailVerified: true));
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, true, true)
            .Returns(string.Equals(path, "relink", StringComparison.Ordinal) ? SignInResult.LockedOut : SignInResult.Failed);
        _userManager.FindByIdAsync(target.Id.ToString()).Returns(target);
        StubVerifiedEmailOwner(email, target);
        _userManager.FindByLoginAsync(Provider, ProviderKey).Returns(new User { Id = Guid.NewGuid(), State = UserState.Merged });
        _userManager.RemoveLoginAsync(Arg.Any<User>(), Provider, ProviderKey).Returns(IdentityResult.Success);
        _userManager.AddLoginAsync(target, Arg.Any<UserLoginInfo>()).Returns(IdentityResult.Success);
        if (string.Equals(path, "session", StringComparison.Ordinal))
            _controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, target.Id.ToString())], "existing-session"));

        var result = await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Login");
        await _userManager.DidNotReceiveWithAnyArgs().AddLoginAsync(default!, default!);
        await _userManager.DidNotReceiveWithAnyArgs().RemoveLoginAsync(default!, default!, default!);
        await _userManager.DidNotReceiveWithAnyArgs().CreateAsync(default!);
        await _userEmailService.DidNotReceiveWithAnyArgs().ReconcileOAuthIdentityAsync(default, default!, default!, default!, default);
    }

    [HumansFact]
    public async Task LockoutRelink_CallsReconcileForActiveTarget()
    {
        var lockedSourceId = Guid.NewGuid();
        var activeTargetId = Guid.NewGuid();
        var email = "relink@example.com";
        var info = MakeInfo(email, emailVerified: true);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, true, true)
            .Returns(SignInResult.LockedOut);

        var lockedSource = new User { Id = lockedSourceId };
        var activeTarget = new User { Id = activeTargetId };
        _userManager.FindByLoginAsync(Provider, ProviderKey).Returns(lockedSource);
        StubVerifiedEmailOwner(email, activeTarget);
        _userManager.RemoveLoginAsync(lockedSource, Provider, ProviderKey)
            .Returns(IdentityResult.Success);
        _userManager.AddLoginAsync(activeTarget, Arg.Any<UserLoginInfo>())
            .Returns(IdentityResult.Success);
        _userManager.UpdateAsync(activeTarget).Returns(IdentityResult.Success);

        await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        await _userEmailService.Received(1).ReconcileOAuthIdentityAsync(
            activeTargetId, Provider, ProviderKey, email,
            claimEmailVerified: true, Arg.Any<CancellationToken>());
        await _userService.Received(1).EnsureStubProfileAsync(activeTargetId, null, null, null,
            Arg.Any<CancellationToken>(), activeTargetId);

        // The relink resolves to a different account than Identity matched; the cookie goes to the active target.
        await _signInManager.Received(1).SignInAsync(activeTarget, true);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task EmailSelectedAccount_UnverifiedProviderClaim_DoesNotLinkOrSignIn(bool lockedOut)
    {
        const string email = "existing@example.com";
        var target = new User { Id = Guid.NewGuid(), State = UserState.Active };
        var source = new User { Id = Guid.NewGuid(), State = UserState.Merged, MergedToUserId = target.Id };
        _signInManager.GetExternalLoginInfoAsync().Returns(MakeInfo(email, emailVerified: false));
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, true, true)
            .Returns(lockedOut ? SignInResult.LockedOut : SignInResult.Failed);
        StubVerifiedEmailOwner(email, target);
        _userManager.FindByLoginAsync(Provider, ProviderKey).Returns(source);
        _userManager.RemoveLoginAsync(source, Provider, ProviderKey).Returns(IdentityResult.Success);
        _userManager.AddLoginAsync(target, Arg.Any<UserLoginInfo>()).Returns(IdentityResult.Success);

        var result = await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Login");
        await _userManager.DidNotReceiveWithAnyArgs().AddLoginAsync(default!, default!);
        await _userManager.DidNotReceiveWithAnyArgs().RemoveLoginAsync(default!, default!, default!);
        await _userManager.DidNotReceiveWithAnyArgs().CreateAsync(default!);
        await _signInManager.DidNotReceiveWithAnyArgs().SignInAsync(default!, default(bool));
        await _userEmailService.DidNotReceiveWithAnyArgs().ReconcileOAuthIdentityAsync(default, default!, default!, default!, default);
    }

    [HumansFact]
    public async Task EmailMatchLink_CallsReconcileForMatchedUser()
    {
        var existingUserId = Guid.NewGuid();
        var email = "linked@example.com";
        var info = MakeInfo(email, emailVerified: true);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, true, true)
            .Returns(SignInResult.Failed);

        var existingUser = new User { Id = existingUserId };
        StubVerifiedEmailOwner(email, existingUser);

        _userManager.AddLoginAsync(existingUser, Arg.Any<UserLoginInfo>())
            .Returns(IdentityResult.Success);
        _userManager.UpdateAsync(existingUser).Returns(IdentityResult.Success);

        await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        await _userEmailService.Received(1).ReconcileOAuthIdentityAsync(
            existingUserId, Provider, ProviderKey, email,
            claimEmailVerified: true, Arg.Any<CancellationToken>());
        await _userService.Received(1).EnsureStubProfileAsync(existingUserId, null, null, null,
            Arg.Any<CancellationToken>(), existingUserId);

        // No login row yet, so Identity's sign-in failed and the callback issues the cookie itself.
        await _signInManager.Received(1).SignInAsync(existingUser, true);
    }

    [HumansFact]
    public async Task NewUserCreation_CallsReconcileForCreatedUserWithVerifiedClaim()
    {
        var newEmail = "fresh@example.com";
        var info = MakeInfo(newEmail, emailVerified: true);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, true, true)
            .Returns(SignInResult.Failed);
        StubVerifiedEmailOwner(newEmail, null);

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
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, true, true)
            .Returns(SignInResult.Failed);
        StubVerifiedEmailOwner(newEmail, null);

        _userManager.CreateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);
        _userManager.AddLoginAsync(Arg.Any<User>(), Arg.Any<UserLoginInfo>())
            .Returns(IdentityResult.Success);
        _userManager.DeleteAsync(Arg.Any<User>()).Returns(IdentityResult.Success);

        _userEmailService.ReconcileOAuthIdentityAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<OAuthReconcileResult>(_ => throw new InvalidOperationException("boom"));

        var result = await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        result.Should().BeOfType<ViewResult>().Which.ViewName.Should().Be("Login");
        _controller.ModelState.Values.SelectMany(value => value.Errors)
            .Should().ContainSingle().Which.ErrorMessage.Should().Be("Login_SetupFailed");

        await _userManager.Received(1).DeleteAsync(Arg.Any<User>());

        // ...and the rollback must not leave a session pointing at a deleted account.
        await _signInManager.DidNotReceive().SignInAsync(
            Arg.Any<User>(), Arg.Any<bool>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task NewUserCreation_CrossUserBlocked_RollsBackUser()
    {
        // Reconcile returns CrossUserBlocked rather than throwing (email_verified false, another user holds the
        // address). The new user must still be rolled back, or an unreachable orphan is left behind.
        var newEmail = "blocked@example.com";
        var info = MakeInfo(newEmail, emailVerified: false);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, true, true)
            .Returns(SignInResult.Failed);
        StubVerifiedEmailOwner(newEmail, null);
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

    [HumansFact]
    public async Task ExistingUserSignInSuccess_UnverifiedEmailClaim_PassedThroughAsFalse()
    {
        var userId = Guid.NewGuid();
        var info = MakeInfo("user@example.com", emailVerified: false);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, true, true)
            .Returns(SignInResult.Success);

        var existingUser = new User { Id = userId };
        _userManager.FindByLoginAsync(Provider, ProviderKey).Returns(existingUser);
        _userManager.UpdateAsync(Arg.Any<User>()).Returns(IdentityResult.Success);

        await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        await _userEmailService.Received(1).ReconcileOAuthIdentityAsync(
            userId, Provider, ProviderKey, "user@example.com",
            claimEmailVerified: false, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task LockoutWithNoActiveTarget_DoesNotSignAnyoneIn()
    {
        // The relink is an exception, not the rule: with nothing to relink to,
        // the locked-out account must stay locked out.
        var email = "locked@example.com";
        var info = MakeInfo(email, emailVerified: true);

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Provider, ProviderKey, true, true)
            .Returns(SignInResult.LockedOut);

        _userManager.FindByLoginAsync(Provider, ProviderKey).Returns(new User { Id = Guid.NewGuid() });
        StubVerifiedEmailOwner(email, null);

        await _controller.ExternalLoginCallback(returnUrl: null, remoteError: null);

        await _signInManager.DidNotReceive().SignInAsync(
            Arg.Any<User>(), Arg.Any<bool>(), Arg.Any<string?>());
        await _userManager.DidNotReceive().CreateAsync(Arg.Any<User>());
    }

    // ExternalLoginService finds the owner via FindByAddressAsync, then loads the entity by id.
    private void StubVerifiedEmailOwner(string email, User? owner)
    {
        _userEmailService.FindByAddressAsync(email, true, true, Arg.Any<CancellationToken>())
            .Returns(owner is null ? [] : [UserEmailFixtures.Row(owner.Id, email)]);
        if (owner is not null)
            _userManager.FindByIdAsync(owner.Id.ToString()).Returns(owner);
    }
}
