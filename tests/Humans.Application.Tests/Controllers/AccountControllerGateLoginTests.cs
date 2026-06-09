using AwesomeAssertions;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
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
/// Gate-terminal sign-in (/Account/GateLogin): the shared gate account
/// (<see cref="SystemUserIds.GateTerminal"/>) signs in with a username +
/// password and gets a persistent session pointed at /Scanner/Tickets.
/// </summary>
public class AccountControllerGateLoginTests
{
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 6, 10, 12, 0));
    private readonly UserManager<User> _userManager;
    private readonly SignInManager<User> _signInManager;
    private readonly AccountController _controller;

    private readonly User _gateUser = new() { Id = SystemUserIds.GateTerminal };

    public AccountControllerGateLoginTests()
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

        var localizer = Substitute.For<IStringLocalizer<Web.SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        _controller = new AccountController(
            _signInManager,
            Substitute.For<IUserService>(),
            _userManager,
            _clock,
            NullLogger<AccountController>.Instance,
            Substitute.For<IUserEmailService>(),
            Substitute.For<IMagicLinkService>(),
            Substitute.For<IAccountProvisioningService>(),
            localizer);

        var tempDataProvider = Substitute.For<ITempDataProvider>();
        _controller.TempData = new TempDataDictionaryFactory(tempDataProvider)
            .GetTempData(new DefaultHttpContext());
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
    }

    private void SetupGateUser() =>
        _userManager.FindByIdAsync(SystemUserIds.GateTerminal.ToString()).Returns(_gateUser);

    [HumansFact]
    public async Task GateLogin_WrongUsername_ReturnsViewWithError()
    {
        var result = await _controller.GateLogin("not-gate", "whatever");

        result.Should().BeOfType<ViewResult>();
        _controller.ModelState.IsValid.Should().BeFalse();
        await _userManager.DidNotReceive().FindByIdAsync(Arg.Any<string>());
    }

    [HumansFact]
    public async Task GateLogin_UnprovisionedAccount_ReturnsViewWithError()
    {
        _userManager.FindByIdAsync(SystemUserIds.GateTerminal.ToString()).Returns((User?)null);

        var result = await _controller.GateLogin(SystemUserIds.GateTerminalLoginName, "whatever");

        result.Should().BeOfType<ViewResult>();
        _controller.ModelState.IsValid.Should().BeFalse();
    }

    [HumansFact]
    public async Task GateLogin_WrongPassword_ReturnsViewWithError_AndDoesNotSignIn()
    {
        SetupGateUser();
        _signInManager.CheckPasswordSignInAsync(_gateUser, "wrong", true)
            .Returns(SignInResult.Failed);

        var result = await _controller.GateLogin(SystemUserIds.GateTerminalLoginName, "wrong");

        result.Should().BeOfType<ViewResult>();
        _controller.ModelState.IsValid.Should().BeFalse();
        await _signInManager.DidNotReceive().SignInAsync(
            Arg.Any<User>(), Arg.Any<bool>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task GateLogin_LockedOut_ReturnsViewWithError()
    {
        SetupGateUser();
        _signInManager.CheckPasswordSignInAsync(_gateUser, "wrong", true)
            .Returns(SignInResult.LockedOut);

        var result = await _controller.GateLogin(SystemUserIds.GateTerminalLoginName, "wrong");

        result.Should().BeOfType<ViewResult>();
        _controller.ModelState.IsValid.Should().BeFalse();
        await _signInManager.DidNotReceive().SignInAsync(
            Arg.Any<User>(), Arg.Any<bool>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task GateLogin_Success_SignsInPersistent_AndRedirectsToScannerTickets()
    {
        SetupGateUser();
        _signInManager.CheckPasswordSignInAsync(_gateUser, "correct", true)
            .Returns(SignInResult.Success);

        var result = await _controller.GateLogin(SystemUserIds.GateTerminalLoginName, "correct");

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be(nameof(ScannerController.Tickets));
        redirect.ControllerName.Should().Be("Scanner");

        // Persistent session so the laptop survives restarts without an admin.
        await _signInManager.Received(1).SignInAsync(_gateUser, true, Arg.Any<string?>());
        _gateUser.LastLoginAt.Should().Be(_clock.GetCurrentInstant());
        await _userManager.Received(1).UpdateAsync(_gateUser);
    }

    [HumansFact]
    public async Task GateLogin_UsernameIsCaseInsensitiveAndTrimmed()
    {
        SetupGateUser();
        _signInManager.CheckPasswordSignInAsync(_gateUser, "correct", true)
            .Returns(SignInResult.Success);

        var result = await _controller.GateLogin("  GATE ", "correct");

        result.Should().BeOfType<RedirectToActionResult>();
    }
}
