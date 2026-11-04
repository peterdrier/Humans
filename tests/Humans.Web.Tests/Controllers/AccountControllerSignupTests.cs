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
        var magicLinks = Substitute.For<IMagicLinkService>();
        magicLinks.VerifySignupToken(token, email).Returns(email);
        magicLinks.VerifyAndConsumeSignupTokenAsync(token, email, Arg.Any<CancellationToken>()).Returns(email);
        var provisioning = Substitute.For<IAccountProvisioningService>();
        provisioning.CompleteMagicLinkSignupAsync(email, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new MagicLinkSignupCompletionResult(MagicLinkSignupCompletionOutcome.Failed, null));
        var localizer = Substitute.For<IStringLocalizer<Base.SharedResource>>();
        localizer[Arg.Any<string>()].Returns(call =>
            new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(call =>
            new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var controller = new AccountController(
            null!, Substitute.For<IUserService>(), Substitute.For<IUserWrite>(), SystemClock.Instance,
            NullLogger<AccountController>.Instance, Substitute.For<IExternalLoginService>(), magicLinks,
            provisioning, null!, new LoginMethodCounter(), localizer)
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
        await magicLinks.DidNotReceive().VerifyAndConsumeSignupTokenAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        provisioning.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task MagicLink_ValidToken_SignsInById()
    {
        var userId = Guid.NewGuid();
        var signIn = SignIn();
        var magicLinks = Substitute.For<IMagicLinkService>();
        magicLinks.VerifyLoginTokenAsync(userId, "login-token", Arg.Any<CancellationToken>()).Returns(true);
        var controller = Controller(signIn, magicLinks, Substitute.For<IAccountProvisioningService>());

        await controller.MagicLink(userId, "login-token", "/");

        await signIn.Received(1).SignInAsync(userId);
    }

    [HumansFact]
    public async Task MagicLink_InvalidToken_DoesNotSignIn()
    {
        var userId = Guid.NewGuid();
        var signIn = SignIn();
        var controller = Controller(signIn, Substitute.For<IMagicLinkService>(),
            Substitute.For<IAccountProvisioningService>());

        var result = await controller.MagicLink(userId, "bad-token", "/");

        result.Should().BeOfType<ViewResult>().Which.ViewName.Should().Be("MagicLinkError");
        await signIn.DidNotReceiveWithAnyArgs().SignInAsync(default);
    }

    [HumansFact]
    public async Task CompleteSignup_Created_SignsInTheCreatedUser()
    {
        const string token = "valid-signup-token";
        const string email = "signup@example.com";
        var userId = Guid.NewGuid();
        var signIn = SignIn();
        var magicLinks = Substitute.For<IMagicLinkService>();
        magicLinks.VerifySignupToken(token, email).Returns(email);
        magicLinks.VerifyAndConsumeSignupTokenAsync(token, email, Arg.Any<CancellationToken>()).Returns(email);
        var provisioning = Substitute.For<IAccountProvisioningService>();
        provisioning.CompleteMagicLinkSignupAsync(email, "Burner", "First", "Last", Arg.Any<CancellationToken>())
            .Returns(new MagicLinkSignupCompletionResult(MagicLinkSignupCompletionOutcome.Created, userId));
        var controller = Controller(signIn, magicLinks, provisioning);

        await controller.CompleteSignup(token, "Burner", "First", "Last", email, "/");

        await signIn.Received(1).SignInAsync(userId);
        magicLinks.DidNotReceive().ReleaseSignupToken(Arg.Any<string>());
    }

    private static IUserSignInService SignIn()
    {
        var signIn = Substitute.For<IUserSignInService>();
        signIn.SignInAsync(Arg.Any<Guid>()).Returns(true);
        return signIn;
    }

    private static AccountController Controller(
        IUserSignInService signIn, IMagicLinkService magicLinks, IAccountProvisioningService provisioning)
    {
        var localizer = Substitute.For<IStringLocalizer<Base.SharedResource>>();
        localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var controller = new AccountController(
            signIn, Substitute.For<IUserService>(), Substitute.For<IUserWrite>(),
            SystemClock.Instance, NullLogger<AccountController>.Instance, Substitute.For<IExternalLoginService>(),
            magicLinks, provisioning, null!, new LoginMethodCounter(), localizer)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            Url = Substitute.For<IUrlHelper>()
        };
        controller.Url.Content(Arg.Any<string>()).Returns(call => call.Arg<string>());
        return controller;
    }
}
