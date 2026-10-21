using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Users.Contracts;
using Humans.Web.Controllers;
using Humans.Web.Services;
using IAuthenticationSchemeProvider = Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
            null!, Substitute.For<IUserService>(), Substitute.For<IUserWrite>(), null!, SystemClock.Instance,
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

    // The contracts hand back ids; the controller loads the Identity user by that id and signs it in.
    [HumansFact]
    public async Task MagicLink_ValidToken_SignsInUserLoadedById()
    {
        var user = new User { Id = Guid.NewGuid() };
        var (userManager, signInManager) = IdentityFakes();
        userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        var magicLinks = Substitute.For<IMagicLinkService>();
        magicLinks.VerifyLoginTokenAsync(user.Id, "login-token", Arg.Any<CancellationToken>()).Returns(true);
        var controller = Controller(signInManager, userManager, magicLinks, Substitute.For<IAccountProvisioningService>());

        await controller.MagicLink(user.Id, "login-token", "/");

        await signInManager.Received(1).SignInAsync(user, true, null);
    }

    [HumansFact]
    public async Task MagicLink_InvalidToken_DoesNotSignIn()
    {
        var userId = Guid.NewGuid();
        var (userManager, signInManager) = IdentityFakes();
        var controller = Controller(signInManager, userManager, Substitute.For<IMagicLinkService>(),
            Substitute.For<IAccountProvisioningService>());

        var result = await controller.MagicLink(userId, "bad-token", "/");

        result.Should().BeOfType<ViewResult>().Which.ViewName.Should().Be("MagicLinkError");
        await userManager.DidNotReceive().FindByIdAsync(Arg.Any<string>());
        await signInManager.DidNotReceive().SignInAsync(Arg.Any<User>(), Arg.Any<bool>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task CompleteSignup_Created_SignsInUserLoadedById()
    {
        const string token = "valid-signup-token";
        const string email = "signup@example.com";
        var user = new User { Id = Guid.NewGuid() };
        var (userManager, signInManager) = IdentityFakes();
        userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        var magicLinks = Substitute.For<IMagicLinkService>();
        magicLinks.VerifySignupToken(token, email).Returns(email);
        magicLinks.VerifyAndConsumeSignupTokenAsync(token, email, Arg.Any<CancellationToken>()).Returns(email);
        var provisioning = Substitute.For<IAccountProvisioningService>();
        provisioning.CompleteMagicLinkSignupAsync(email, "Burner", "First", "Last", Arg.Any<CancellationToken>())
            .Returns(new MagicLinkSignupCompletionResult(MagicLinkSignupCompletionOutcome.Created, user.Id));
        var controller = Controller(signInManager, userManager, magicLinks, provisioning);

        await controller.CompleteSignup(token, "Burner", "First", "Last", email, "/");

        await signInManager.Received(1).SignInAsync(user, true, null);
        magicLinks.DidNotReceive().ReleaseSignupToken(Arg.Any<string>());
    }

    private static (UserManager<User>, SignInManager<User>) IdentityFakes()
    {
        var userManager = Substitute.For<UserManager<User>>(
            Substitute.For<IUserStore<User>>(), null, null, null, null, null, null, null, null);
        var options = Substitute.For<IOptions<IdentityOptions>>();
        options.Value.Returns(new IdentityOptions());
        var signInManager = Substitute.For<SignInManager<User>>(
            userManager, Substitute.For<IHttpContextAccessor>(), Substitute.For<IUserClaimsPrincipalFactory<User>>(),
            options, NullLogger<SignInManager<User>>.Instance, Substitute.For<IAuthenticationSchemeProvider>(),
            Substitute.For<IUserConfirmation<User>>());
        return (userManager, signInManager);
    }

    private static AccountController Controller(
        SignInManager<User> signInManager, UserManager<User> userManager,
        IMagicLinkService magicLinks, IAccountProvisioningService provisioning)
    {
        var localizer = Substitute.For<IStringLocalizer<Base.SharedResource>>();
        localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        var controller = new AccountController(
            signInManager, Substitute.For<IUserService>(), Substitute.For<IUserWrite>(), userManager,
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
