using Humans.Auth.Contracts;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Web.Controllers;
using Humans.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Humans.Users.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Humans.Users.Contracts;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Failures are throttled per source IP, never per account: an attacker must not be able to lock out an account whose
/// username is public.
/// </summary>
public class AccountControllerGateSignInTests
{
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 6, 10, 12, 0));
    private readonly UserManager<User> _userManager;
    private readonly SignInManager<User> _signInManager;
    private readonly IUserWrite _userService = Substitute.For<IUserWrite>();
    private readonly AccountController _controller;

    private readonly User _gateUser = new() { Id = SystemUserIds.GateTerminal };

    public AccountControllerGateSignInTests()
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

        var localizer = Substitute.For<IStringLocalizer<Base.SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        var throttle = new GateSignInThrottle(
            new MemoryCache(new MemoryCacheOptions()), _clock);

        _controller = new AccountController(
            new UserSignInService(_signInManager, _userManager, Substitute.For<IServiceScopeFactory>()),
            Substitute.For<IUserService>(),
            _userService,
            _clock,
            FixedOrgTimeZone.Madrid,
            NullLogger<AccountController>.Instance,
            Substitute.For<IExternalLoginService>(),
            Substitute.For<ISignInLinkService>(),
            Substitute.For<IAccountProvisioningService>(),
            throttle,
            new SignInMethodCounter(),
            localizer);

        var tempDataProvider = Substitute.For<ITempDataProvider>();
        _controller.TempData = new TempDataDictionaryFactory(tempDataProvider)
            .GetTempData(new DefaultHttpContext());
        _controller.ControllerContext = new ControllerContext
        {
            // RemoteIpAddress is null here, so every attempt shares the "unknown" throttle bucket.
            HttpContext = new DefaultHttpContext()
        };
    }

    private void SetupGateUser() =>
        _userManager.FindByIdAsync(SystemUserIds.GateTerminal.ToString()).Returns(_gateUser);

    [HumansFact]
    public async Task GateSignIn_WrongUsername_ReturnsViewWithError()
    {
        var result = await _controller.GateSignIn("not-gate", "whatever");

        result.Should().BeOfType<ViewResult>();
        _controller.ModelState.IsValid.Should().BeFalse();
        await _userManager.DidNotReceive().FindByIdAsync(Arg.Any<string>());
    }

    [HumansFact]
    public async Task GateSignIn_UnprovisionedAccount_ReturnsViewWithError()
    {
        _userManager.FindByIdAsync(SystemUserIds.GateTerminal.ToString()).Returns((User?)null);

        var result = await _controller.GateSignIn(SystemUserIds.GateTerminalLoginName, "whatever");

        result.Should().BeOfType<ViewResult>();
        _controller.ModelState.IsValid.Should().BeFalse();
    }

    [HumansFact]
    public async Task GateSignIn_WrongPassword_ReturnsViewWithError_AndDoesNotSignIn()
    {
        SetupGateUser();
        _signInManager.CheckPasswordSignInAsync(_gateUser, "wrong", false)
            .Returns(SignInResult.Failed);

        var result = await _controller.GateSignIn(SystemUserIds.GateTerminalLoginName, "wrong");

        result.Should().BeOfType<ViewResult>();
        _controller.ModelState.IsValid.Should().BeFalse();
        await _signInManager.DidNotReceive().SignInAsync(
            Arg.Any<User>(), Arg.Any<bool>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task GateSignIn_NeverUsesIdentityLockout()
    {
        SetupGateUser();
        _signInManager.CheckPasswordSignInAsync(_gateUser, "wrong", false)
            .Returns(SignInResult.Failed);

        await _controller.GateSignIn(SystemUserIds.GateTerminalLoginName, "wrong");

        // The shared account must never be lockable — lockoutOnFailure stays false.
        await _signInManager.DidNotReceive().CheckPasswordSignInAsync(
            Arg.Any<User>(), Arg.Any<string>(), true);
    }

    [HumansFact]
    public async Task GateSignIn_TooManyFailures_ThrottlesBeforePasswordCheck()
    {
        SetupGateUser();
        _signInManager.CheckPasswordSignInAsync(_gateUser, Arg.Any<string>(), false)
            .Returns(SignInResult.Failed);

        for (var i = 0; i < GateSignInThrottle.MaxFailuresPerWindow; i++)
        {
            await _controller.GateSignIn(SystemUserIds.GateTerminalLoginName, $"wrong-{i}");
        }

        _signInManager.ClearReceivedCalls();
        _controller.ModelState.Clear();

        var result = await _controller.GateSignIn(SystemUserIds.GateTerminalLoginName, "would-be-correct");

        result.Should().BeOfType<ViewResult>();
        _controller.ModelState.IsValid.Should().BeFalse();
        _controller.ModelState[string.Empty]!.Errors.Single().ErrorMessage
            .Should().Contain("GateSignIn_Throttled");
        await _signInManager.DidNotReceive().CheckPasswordSignInAsync(
            Arg.Any<User>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [HumansFact]
    public async Task GateSignIn_ThrottleExpires_AfterTheWindow()
    {
        SetupGateUser();
        _signInManager.CheckPasswordSignInAsync(_gateUser, Arg.Any<string>(), false)
            .Returns(SignInResult.Failed);

        for (var i = 0; i < GateSignInThrottle.MaxFailuresPerWindow; i++)
        {
            await _controller.GateSignIn(SystemUserIds.GateTerminalLoginName, $"wrong-{i}");
        }

        _clock.Advance(GateSignInThrottle.Window + Duration.FromSeconds(1));
        _signInManager.CheckPasswordSignInAsync(_gateUser, "correct", false)
            .Returns(SignInResult.Success);
        _controller.ModelState.Clear();

        var result = await _controller.GateSignIn(SystemUserIds.GateTerminalLoginName, "correct");

        result.Should().BeOfType<RedirectToActionResult>();
    }

    [HumansFact]
    public async Task GateSignIn_SuccessResetsTheThrottleForTheSource()
    {
        SetupGateUser();
        _signInManager.CheckPasswordSignInAsync(_gateUser, "wrong", false)
            .Returns(SignInResult.Failed);
        _signInManager.CheckPasswordSignInAsync(_gateUser, "correct", false)
            .Returns(SignInResult.Success);

        for (var i = 0; i < GateSignInThrottle.MaxFailuresPerWindow - 1; i++)
        {
            await _controller.GateSignIn(SystemUserIds.GateTerminalLoginName, "wrong");
        }

        (await _controller.GateSignIn(SystemUserIds.GateTerminalLoginName, "correct"))
            .Should().BeOfType<RedirectToActionResult>();

        // The success wiped the failure history: another failure doesn't throttle.
        _controller.ModelState.Clear();
        await _controller.GateSignIn(SystemUserIds.GateTerminalLoginName, "wrong");
        _controller.ModelState[string.Empty]!.Errors.Single().ErrorMessage
            .Should().Contain("GateSignIn_Invalid");
    }

    [HumansFact]
    public async Task GateSignIn_Success_SignsInPersistent_AndRedirectsToGateTerminal()
    {
        SetupGateUser();
        _signInManager.CheckPasswordSignInAsync(_gateUser, "correct", false)
            .Returns(SignInResult.Success);

        var result = await _controller.GateSignIn(SystemUserIds.GateTerminalLoginName, "correct");

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        // A literal: GateController is internal to Humans.Gate.
        redirect.ActionName.Should().Be("Index");
        redirect.ControllerName.Should().Be("Gate");

        // Persistent session so the laptop survives restarts without an admin.
        await _signInManager.Received(1).SignInAsync(_gateUser, true, Arg.Any<string?>());
        await _userService.Received(1).RecordSignInAsync(_gateUser.Id, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GateSignIn_UsernameIsCaseInsensitiveAndTrimmed()
    {
        SetupGateUser();
        _signInManager.CheckPasswordSignInAsync(_gateUser, "correct", false)
            .Returns(SignInResult.Success);

        var result = await _controller.GateSignIn("  GATE ", "correct");

        result.Should().BeOfType<RedirectToActionResult>();
    }
}
