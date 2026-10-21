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
}
