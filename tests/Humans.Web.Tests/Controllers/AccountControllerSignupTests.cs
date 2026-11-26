using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Users.Contracts;
using Humans.Web.Controllers;
using Humans.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Web.Tests.Controllers;

public sealed class AccountControllerSignupTests
{
    [HumansTheory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(1)]
    [Xunit.InlineData(2)]
    public async Task CompleteSignup_OverlongName_PreservesInputWithoutRedeemingToken(int field)
    {
        const string token = "valid-signup-token";
        const string email = "signup@example.com";
        var signInLinks = Substitute.For<ISignInLinkService>();
        signInLinks.VerifySignupToken(token, email).Returns(new SignInLinkSignupCheck(email, null));
        signInLinks.ConsumeSignupTokenAsync(token, email, Arg.Any<CancellationToken>()).Returns(new SignInLinkSignupCheck(email, null));
        var provisioning = Substitute.For<IAccountProvisioningService>();
        provisioning.CompleteSignInLinkSignupAsync(email, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new SignInLinkSignupResult(SignInLinkSignupOutcome.Failed, null));
        var localizer = Substitute.For<IStringLocalizer<Base.SharedResource>>();
        localizer[Arg.Any<string>()].Returns(call =>
            new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(call =>
            new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var controller = new AccountController(
            null!, Substitute.For<IUserService>(), Substitute.For<IUserWrite>(), SystemClock.Instance,
            FixedOrgTimeZone.Madrid,
            NullLogger<AccountController>.Instance, Substitute.For<IExternalLoginService>(), signInLinks,
            provisioning, null!, new SignInMethodCounter(), localizer)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        string[] names = ["Burner", "First", "Last"];
        names[field] = new string('x', 257);

        var result = await controller.CompleteSignup(token, names[0], names[1], names[2], email, "/Calendar");

        result.Should().BeOfType<ViewResult>().Which.ViewName.Should().Be("CompleteSignup");
        controller.ModelState.IsValid.Should().BeFalse();
        controller.ModelState[new[] { "burnerName", "firstName", "lastName" }[field]]!
            .Errors.Should().ContainSingle(error => string.Equals(error.ErrorMessage, "Validation_MaxLength", StringComparison.Ordinal));
        controller.ViewData["Token"].Should().Be(token);
        controller.ViewData["Email"].Should().Be(email);
        controller.ViewData["ReturnUrl"].Should().Be("/Calendar");
        controller.ViewData["BurnerName"].Should().Be(names[0]);
        controller.ViewData["FirstName"].Should().Be(names[1]);
        controller.ViewData["LastName"].Should().Be(names[2]);
        await signInLinks.DidNotReceive().ConsumeSignupTokenAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        provisioning.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task SignInLink_ValidToken_SignsInById()
    {
        var userId = Guid.NewGuid();
        var signIn = SignIn();
        var signInLinks = Substitute.For<ISignInLinkService>();
        signInLinks.VerifySignInTokenAsync(userId, "login-token", Arg.Any<CancellationToken>()).Returns((SignInLinkFailure?)null);
        var controller = Controller(signIn, signInLinks, Substitute.For<IAccountProvisioningService>());

        await controller.SignInLink(userId, "login-token", "/");

        await signIn.Received(1).SignInAsync(userId);
    }

    [HumansFact]
    public async Task SignInLink_InvalidToken_DoesNotSignIn()
    {
        var userId = Guid.NewGuid();
        var signIn = SignIn();
        var signInLinks = Substitute.For<ISignInLinkService>();
        signInLinks.VerifySignInTokenAsync(userId, "bad-token", Arg.Any<CancellationToken>()).Returns(SignInLinkFailure.Expired);
        var controller = Controller(signIn, signInLinks, Substitute.For<IAccountProvisioningService>());

        var result = await controller.SignInLink(userId, "bad-token", "/");

        var view = result.Should().BeOfType<ViewResult>().Which;
        view.ViewName.Should().Be("SignInLinkError");
        view.Model.Should().Be("expired");
        await signIn.DidNotReceiveWithAnyArgs().SignInAsync(default);
    }

    [HumansFact]
    public async Task CompleteSignup_Created_SignsInTheCreatedUser()
    {
        const string token = "valid-signup-token";
        const string email = "signup@example.com";
        var userId = Guid.NewGuid();
        var signIn = SignIn();
        var signInLinks = Substitute.For<ISignInLinkService>();
        signInLinks.VerifySignupToken(token, email).Returns(new SignInLinkSignupCheck(email, null));
        signInLinks.ConsumeSignupTokenAsync(token, email, Arg.Any<CancellationToken>()).Returns(new SignInLinkSignupCheck(email, null));
        var provisioning = Substitute.For<IAccountProvisioningService>();
        provisioning.CompleteSignInLinkSignupAsync(email, "Burner", "First", "Last", Arg.Any<CancellationToken>())
            .Returns(new SignInLinkSignupResult(SignInLinkSignupOutcome.Created, userId));
        var controller = Controller(signIn, signInLinks, provisioning);

        await controller.CompleteSignup(token, "Burner", "First", "Last", email, "/");

        await signIn.Received(1).SignInAsync(userId);
        signInLinks.DidNotReceive().ReleaseSignupToken(Arg.Any<string>());
    }

    private static IUserSignInService SignIn()
    {
        var signIn = Substitute.For<IUserSignInService>();
        signIn.SignInAsync(Arg.Any<Guid>()).Returns(true);
        return signIn;
    }

    private static AccountController Controller(
        IUserSignInService signIn, ISignInLinkService signInLinks, IAccountProvisioningService provisioning)
    {
        var localizer = Substitute.For<IStringLocalizer<Base.SharedResource>>();
        localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var controller = new AccountController(
            signIn, Substitute.For<IUserService>(), Substitute.For<IUserWrite>(),
            SystemClock.Instance, FixedOrgTimeZone.Madrid, NullLogger<AccountController>.Instance, Substitute.For<IExternalLoginService>(),
            signInLinks, provisioning, null!, new SignInMethodCounter(), localizer)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            Url = Substitute.For<IUrlHelper>()
        };
        controller.Url.Content(Arg.Any<string>()).Returns(call => call.Arg<string>());
        return controller;
    }
}
