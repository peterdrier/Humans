using Humans.Base.Extensions;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;
using Xunit;
using Humans.Users.Services;
using Humans.Users.Models;
using Humans.Users.Controllers;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Tickets.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Users.Contracts;

using Humans.Users.Tests.Infrastructure;
using Humans.Base;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;

using Humans.Google.Contracts;

namespace Humans.Users.Tests.Controllers;

public class ProfileEmailsControllerGridTests
{
    private readonly ILogger<ProfileEmailsController> _logger = Substitute.For<ILogger<ProfileEmailsController>>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IAuthorizationService _authorizationService = Substitute.For<IAuthorizationService>();
    private readonly IUserManagement _userService = Substitute.For<IUserManagement>();
    private readonly UserManager<User> _userManager;
    private readonly SignInManager<User> _signInManager;
    private readonly ProfileEmailsController _controller;
    private readonly IStringLocalizer<SharedResource> _sharedLocalizer = Substitute.For<IStringLocalizer<SharedResource>>();
    private readonly Guid _userId = Guid.NewGuid();

    public ProfileEmailsControllerGridTests()
    {
        _userEmailService.SetPrimaryAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>(), _userId)
            .Returns(new UpdateEmailResult(true));
        _userEmailService.SetVisibilityAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<ContactFieldVisibility?>(), Arg.Any<CancellationToken>(), _userId)
            .Returns(new UpdateEmailResult(true));
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

        var localizer = Substitute.For<IStringLocalizer<UsersResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        localizer["Validation_EmailAddress"].Returns(new LocalizedString("Validation_EmailAddress", "Validation_EmailAddress", resourceNotFound: true));

        _sharedLocalizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        _controller = new ProfileEmailsController(
            _userService,
            _userManager,
            _userEmailService,
            _logger,
            localizer,
            _sharedLocalizer,
            Substitute.For<ITicketService>(),
            _authorizationService,
            _signInManager,
            Options.Create(new GoogleWorkspaceOptions()));

        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, _userId.ToString())
        ], authenticationType: "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions.AddLogging(services);
        var httpContext = new DefaultHttpContext
        {
            User = principal,
            RequestServices = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services),
        };
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
            ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor { ActionName = "Test" },
            RouteData = new Microsoft.AspNetCore.Routing.RouteData(),
        };
        _controller.TempData = new TempDataDictionary(httpContext, Substitute.For<ITempDataProvider>());
        _controller.Url = Substitute.For<IUrlHelper>();
        _controller.Url.Action(Arg.Any<UrlActionContext>()).Returns("/Profile/Me/Emails");

        _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>())
            .Returns(new User { Id = _userId });
        _userManager.GetUserId(Arg.Any<ClaimsPrincipal>()).Returns(_userId.ToString());

        // A minimal UserInfo for the test user, so the actions pass the null-guard.
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(new User { Id = _userId }.ToUserInfo()));

        _authorizationService.AuthorizeAsync(
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<object?>(),
            Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
    }

    public enum EmailMutation
    {
        SetEmailVisibility,
        DeleteEmail,
        LinkGoogle
    }

    [HumansTheory]
    [Xunit.InlineData(EmailMutation.SetEmailVisibility)]
    [Xunit.InlineData(EmailMutation.DeleteEmail)]
    [Xunit.InlineData(EmailMutation.LinkGoogle)]
    public async Task Email_mutation_account_preflight_observes_cancellation(EmailMutation action)
    {
        using var request = new CancellationTokenSource();
        _controller.HttpContext.RequestAborted = request.Token;
        _userService.GetUserInfoAsync(_userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().Should().Be(request.Token);
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new ValueTask<UserInfo?>(new User { Id = _userId }.ToUserInfo());
        });
        await request.CancelAsync();
        var emailId = Guid.NewGuid();
        Func<Task<IActionResult>> invoke = action switch
        {
            EmailMutation.SetEmailVisibility => () => _controller.SetEmailVisibility(emailId, null),
            EmailMutation.DeleteEmail => () => _controller.DeleteEmail(emailId),
            EmailMutation.LinkGoogle => () => _controller.Link("Google"),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };

        await invoke.Should().ThrowAsync<OperationCanceledException>();
        _authorizationService.ReceivedCalls().Should().BeEmpty();
        _userEmailService.ReceivedCalls().Should().BeEmpty();
    }

    public enum AdminVerificationOutcome { NoPending, Refused, Verified, MergeRequested }

    [HumansTheory]
    [InlineData(AdminVerificationOutcome.NoPending)]
    [InlineData(AdminVerificationOutcome.Refused)]
    [InlineData(AdminVerificationOutcome.Verified)]
    [InlineData(AdminVerificationOutcome.MergeRequested)]
    public async Task AdminVerifyEmail_FormatsExplicitOutcome(AdminVerificationOutcome outcome)
    {
        var ct = TestContext.Current.CancellationToken;
        var row = Guid.NewGuid();
        var target = Guid.NewGuid();
        _userEmailService.AdminMarkVerifiedAsync(target, row, _userId, ct).Returns(
            new VerifyEmailResult(outcome == AdminVerificationOutcome.NoPending ? string.Empty : "email@example.test",
                outcome == AdminVerificationOutcome.MergeRequested,
                outcome == AdminVerificationOutcome.Refused ? "Users_EmailUpdate_AccountUnavailable" : null)
            { NoPendingVerification = outcome == AdminVerificationOutcome.NoPending });

        var result = await _controller.AdminVerifyEmail(target, row, ct);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be(nameof(ProfileEmailsController.AdminEmails));
        redirect.RouteValues!["id"].Should().Be(target);
        var refused = outcome is AdminVerificationOutcome.NoPending or AdminVerificationOutcome.Refused;
        _controller.TempData.ContainsKey("ErrorMessage").Should().Be(refused);
        _controller.TempData.ContainsKey("SuccessMessage").Should().Be(!refused);
        if (refused)
        {
            _controller.TempData["ErrorMessage"].Should().Be(outcome == AdminVerificationOutcome.NoPending
                ? "No email pending verification." : "Users_EmailUpdate_AccountUnavailable");
            var log = _logger.ReceivedCalls().Single().GetArguments();
            log[0].Should().Be(LogLevel.Warning);
            log[3].Should().BeNull();
        }
        else
        {
            _controller.TempData["SuccessMessage"].Should().Be(outcome == AdminVerificationOutcome.MergeRequested
                ? "Users_EmailGrid_AdminVerifyMergeRequested" : "Users_EmailGrid_AdminVerifySuccess");
            _logger.ReceivedCalls().Should().BeEmpty();
        }
    }

    public enum AdminVerificationDependencyFailure { Validation, Operation }

    [HumansTheory]
    [InlineData(AdminVerificationDependencyFailure.Validation)]
    [InlineData(AdminVerificationDependencyFailure.Operation)]
    public async Task AdminVerifyEmail_PropagatesDependencyFailures(AdminVerificationDependencyFailure kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var row = Guid.NewGuid();
        Exception failure = kind == AdminVerificationDependencyFailure.Validation
            ? new ValidationException("Private dependency diagnostic")
            : new InvalidOperationException("Private dependency diagnostic");
        _userEmailService.AdminMarkVerifiedAsync(_userId, row, _userId, ct)
            .Returns(Task.FromException<VerifyEmailResult>(failure));

        var act = () => _controller.AdminVerifyEmail(_userId, row, ct);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
        _controller.TempData.Should().BeEmpty();
        _logger.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task Emails_RequestAborted_CancelsEmailGridReads()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        _controller.HttpContext.RequestAborted = aborted.Token;
        _userManager.GetLoginsAsync(Arg.Any<User>()).Returns([]);
        _userEmailService.GetUserEmailsAsync(_userId, Arg.Any<CancellationToken>()).Returns([]);
        _userEmailService.GetUserEmailsAsync(_userId, aborted.Token)
            .Returns(Task.FromException<IReadOnlyList<UserEmailEditDto>>(new OperationCanceledException(aborted.Token)));

        var read = () => _controller.Emails();

        await read.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansTheory]
    [InlineData("SetPrimary")]
    [InlineData("SetEmailVisibility")]
    [InlineData("DeleteEmail")]
    [InlineData("ClearPrimary")]
    [InlineData("Unlink")]
    [InlineData("AdminSetPrimary")]
    [InlineData("AdminSetVisibility")]
    [InlineData("AdminDeleteEmail")]
    [InlineData("AdminClearPrimary")]
    [InlineData("AdminUnlink")]
    public async Task EmailMutation_RefusalLogsWarningWithReasonWithoutException(string action)
    {
        const string reason = "Email mutation rejected by a guardrail";
        _userEmailService.SetPrimaryAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>(), _userId)
            .Returns(new UpdateEmailResult(false, reason));
        _userEmailService.SetVisibilityAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<ContactFieldVisibility?>(), Arg.Any<CancellationToken>(), _userId)
            .Returns(new UpdateEmailResult(false, reason));
        _userEmailService.DeleteEmailAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>(), _userId)
            .Returns(new DeleteEmailResult(false, reason));
        _userEmailService.ClearPrimaryAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _userEmailService.UnlinkAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new UnlinkEmailResult(false, reason));
        var emailId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        var result = action switch
        {
            "SetPrimary" => await _controller.SetPrimary(emailId, ct),
            "SetEmailVisibility" => await _controller.SetEmailVisibility(emailId, null),
            "DeleteEmail" => await _controller.DeleteEmail(emailId),
            "ClearPrimary" => await _controller.ClearPrimary(emailId, ct),
            "Unlink" => await _controller.Unlink(emailId, ct),
            "AdminSetPrimary" => await _controller.AdminSetPrimary(targetId, emailId, ct),
            "AdminSetVisibility" => await _controller.AdminSetVisibility(targetId, emailId, null, ct),
            "AdminDeleteEmail" => await _controller.AdminDeleteEmail(targetId, emailId, ct),
            "AdminClearPrimary" => await _controller.AdminClearPrimary(targetId, emailId, ct),
            "AdminUnlink" => await _controller.AdminUnlink(targetId, emailId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        result.Should().BeOfType<RedirectToActionResult>();
        var expectedError = action is "ClearPrimary" or "AdminClearPrimary"
            ? "Users_EmailGrid_ClearPrimaryRejected"
            : reason;
        _controller.TempData["ErrorMessage"].Should().Be(expectedError);
        var arguments = _logger.ReceivedCalls().Should().ContainSingle(call =>
            string.Equals(call.GetMethodInfo().Name, "Log", StringComparison.Ordinal)).Subject.GetArguments();
        arguments[0].Should().Be(LogLevel.Warning);
        arguments[3].Should().BeNull();
        arguments[2]!.ToString().Should().Contain(expectedError).And.Contain(emailId.ToString());
    }

    [HumansFact]
    public async Task VerifyEmail_LocalizesTypedInvalidLink()
    {
        using var cultureScope = new CultureScope("en");
        var localizer = new StringLocalizer<UsersResource>(new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance));
        var controller = new ProfileEmailsController(
            _userService, _userManager, _userEmailService,
            _logger, localizer, _sharedLocalizer, Substitute.For<ITicketService>(),
            _authorizationService, _signInManager, Options.Create(new GoogleWorkspaceOptions()));
        var row = Guid.NewGuid();
        _userEmailService.VerifyEmailAsync(_userId, row, "token")
            .Returns(new VerifyEmailResult(string.Empty, false, "Users_Profile_InvalidVerificationLink"));

        var result = await controller.VerifyEmail(_userId, row, "token");

        result.Should().BeOfType<ViewResult>().Which.ViewName.Should().Be("VerifyEmailResult");
        controller.ViewData["Success"].Should().Be(false);
        controller.ViewData["Message"].Should().Be(localizer["Users_Profile_InvalidVerificationLink"].Value);
        controller.ViewData["Message"].Should().NotBe("Users_Profile_InvalidVerificationLink");
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyEmail_PropagatesDependenciesInsteadOfInvalidLinkFeedback(bool validationException)
    {
        var row = Guid.NewGuid();
        Exception failure = validationException
            ? new ValidationException("Private dependency validation diagnostic")
            : new InvalidOperationException("Private dependency operation diagnostic");
        _userEmailService.VerifyEmailAsync(_userId, row, "token").Returns(Task.FromException<VerifyEmailResult>(failure));

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => _controller.VerifyEmail(_userId, row, "token"));

        thrown.Should().BeSameAs(failure);
        _controller.ViewData.ContainsKey("Message").Should().BeFalse();
        _logger.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EmailUpdate_RefusalIsLocalizedWithoutSuccessAudit(bool adminRoute, bool primary)
    {
        var emailId = Guid.NewGuid();
        var targetId = adminRoute ? Guid.NewGuid() : _userId;
        var errorKey = primary ? "Users_EmailUpdate_UnverifiedTarget" : "Users_EmailUpdate_NotFound";
        _userEmailService.SetPrimaryAsync(targetId, emailId, Arg.Any<CancellationToken>(), _userId)
            .Returns(new UpdateEmailResult(false, errorKey));
        _userEmailService.SetVisibilityAsync(targetId, emailId, Arg.Any<ContactFieldVisibility?>(), Arg.Any<CancellationToken>(), _userId)
            .Returns(new UpdateEmailResult(false, errorKey));
        var ct = TestContext.Current.CancellationToken;

        var result = (adminRoute, primary) switch
        {
            (true, true) => await _controller.AdminSetPrimary(targetId, emailId, ct),
            (true, false) => await _controller.AdminSetVisibility(targetId, emailId, null, ct),
            (false, true) => await _controller.SetPrimary(emailId, ct),
            (false, false) => await _controller.SetEmailVisibility(emailId, null)
        };

        result.Should().BeOfType<RedirectToActionResult>();
        _controller.TempData["ErrorMessage"].Should().Be(errorKey);
        _controller.TempData["SuccessMessage"].Should().BeNull();
    }

    [HumansFact]
    public async Task SetPrimary_UnexpectedDependencyFailure_Propagates()
    {
        var emailId = Guid.NewGuid();
        _userEmailService.SetPrimaryAsync(_userId, emailId, Arg.Any<CancellationToken>(), _userId)
            .Returns(Task.FromException<UpdateEmailResult>(new IOException("email store unavailable")));

        var action = () => _controller.SetPrimary(emailId, TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<IOException>().WithMessage("email store unavailable");
        _logger.ReceivedCalls().Should().NotContain(call =>
            string.Equals(call.GetMethodInfo().Name, "Log", StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task Link_AsSelf_ReturnsChallengeResult_WithProvider()
    {
        // Override the callback-specific Url.Action so the RedirectUri assertion below is meaningful.
        _controller.Url.Action(Arg.Is<UrlActionContext>(ctx =>
                ctx.Action == "ExternalLoginCallback" && ctx.Controller == "Account"))
            .Returns("/Account/ExternalLoginCallback?returnUrl=%2FProfile%2FMe%2FEmails");

        AuthenticationProperties? capturedProps = null;
        _signInManager.ConfigureExternalAuthenticationProperties("Google", Arg.Any<string>())
            .Returns(ci =>
            {
                capturedProps = new AuthenticationProperties { RedirectUri = ci.ArgAt<string>(1) };
                return capturedProps;
            });

        var result = await _controller.Link("Google", returnUrl: "/Profile/Me/Emails");

        result.Should().BeOfType<ChallengeResult>()
            .Which.AuthenticationSchemes.Should().Contain("Google");
        // The Link flow MUST route through AccountController.ExternalLoginCallback
        // so the link-while-signed-in branch (UserManager.AddLoginAsync) fires.
        // Going straight to /Profile/Me/Emails would skip that branch.
        capturedProps.Should().NotBeNull();
        capturedProps!.RedirectUri.Should().Contain("ExternalLoginCallback");
    }

    [HumansFact]
    public async Task Link_AuthorizationFails_ReturnsForbid()
    {
        _authorizationService.AuthorizeAsync(
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<object?>(),
            Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var result = await _controller.Link("Google", returnUrl: null);

        result.Should().BeOfType<ForbidResult>();
    }

    [HumansFact]
    public async Task Unlink_LastSignInRefusal_UsesLocalizedResult()
    {
        var emailId = Guid.NewGuid();
        _userEmailService.UnlinkAsync(_userId, emailId, _userId, Arg.Any<CancellationToken>())
            .Returns(new UnlinkEmailResult(false, "Users_LinkedAccounts_UnlinkBlockedLastSignInMethod"));

        var result = await _controller.Unlink(emailId, TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Emails");
        _controller.TempData["ErrorMessage"].Should().Be("Users_LinkedAccounts_UnlinkBlockedLastSignInMethod");
        await _userManager.DidNotReceive().RemoveLoginAsync(Arg.Any<User>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task Unlink_AsSelf_CallsUnlinkAsync_AndRedirectsToGrid()
    {
        var emailId = Guid.NewGuid();
        _userEmailService.UnlinkAsync(_userId, emailId, _userId, Arg.Any<CancellationToken>())
            .Returns(new UnlinkEmailResult(true));

        var result = await _controller.Unlink(emailId, TestContext.Current.CancellationToken);

        await _userEmailService.Received(1).UnlinkAsync(
            _userId, emailId, _userId, Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("Emails");
    }

    [HumansFact]
    public async Task Unlink_AuthorizationFails_ReturnsForbid()
    {
        var emailId = Guid.NewGuid();
        _authorizationService.AuthorizeAsync(
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<object?>(),
            Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var result = await _controller.Unlink(emailId, TestContext.Current.CancellationToken);

        result.Should().BeOfType<ForbidResult>();
        await _userEmailService.DidNotReceive().UnlinkAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // Admin grid actions: the route userId is the target, the signed-in user is the actor.

    [HumansFact]
    public async Task AdminSetPrimary_AsAdmin_CallsServiceWithRouteUserId()
    {
        var targetUserId = Guid.NewGuid();
        var emailId = Guid.NewGuid();

        var result = await _controller.AdminSetPrimary(targetUserId, emailId, TestContext.Current.CancellationToken);

        await _userEmailService.Received(1).SetPrimaryAsync(
            targetUserId, emailId, Arg.Any<CancellationToken>(), _userId);
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("AdminEmails");
    }

    [HumansTheory]
    [InlineData("Validation_EmailAddress", false)]
    [InlineData("Users_Profile_EmailAlreadyAdded", false)]
    [InlineData("Users_Profile_EmailMergePending", false)]
    [InlineData("Validation_EmailAddress", true)]
    [InlineData("Users_Profile_EmailAlreadyAdded", true)]
    [InlineData("Users_Profile_EmailMergePending", true)]
    public async Task AddEmail_ExpectedRefusal_DoesNotSendOrAudit(string errorKey, bool admin)
    {
        const string email = "new@example.com";
        _userManager.FindByIdAsync(_userId.ToString()).Returns(new User { Id = _userId });
        _userEmailService.AddEmailAsync(_userId, email, Arg.Any<CancellationToken>(), _userId)
            .Returns(new AddEmailResult(Guid.Empty, string.Empty, false, errorKey));
        _sharedLocalizer["Validation_EmailAddress"].Returns(new LocalizedString("Validation_EmailAddress", "Invalid email"));
        var expectedMessage = string.Equals(errorKey, "Validation_EmailAddress", StringComparison.Ordinal)
            ? "Invalid email" : errorKey;

        if (admin)
        {
            var result = await _controller.AdminAddEmail(_userId, email, TestContext.Current.CancellationToken);
            result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("AdminEmails");
            _controller.TempData["ErrorMessage"].Should().Be(expectedMessage);
        }
        else
        {
            var result = await _controller.AddEmail(new EmailsViewModel { NewEmail = email });
            result.Should().BeOfType<ViewResult>().Which.ViewName.Should().Be("Emails");
            _controller.ModelState[nameof(EmailsViewModel.NewEmail)]!.Errors.Single().ErrorMessage.Should().Be(expectedMessage);
        }

    }

    [HumansFact]
    public async Task AdminAddEmail_AsAdmin_CallsServiceWithRouteUserId()
    {
        var targetUserId = Guid.NewGuid();
        const string newEmail = "added@example.com";
        _userManager.FindByIdAsync(targetUserId.ToString())
            .Returns(new User { Id = targetUserId, DisplayName = "Target User", PreferredLanguage = "en" });
        _userEmailService.AddEmailAsync(targetUserId, newEmail, Arg.Any<CancellationToken>(), _userId)
            .Returns(new AddEmailResult(Guid.NewGuid(), "token", IsConflict: false));

        var result = await _controller.AdminAddEmail(targetUserId, newEmail, TestContext.Current.CancellationToken);

        await _userEmailService.Received(1).AddEmailAsync(
            targetUserId, newEmail, Arg.Any<CancellationToken>(), _userId);
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("AdminEmails");
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdminEmailAddition_ExpectedRefusal_DoesNotAuditOrSend(bool verified)
    {
        var targetId = Guid.NewGuid();
        const string email = "added@example.com";
        const string errorKey = "Users_EmailUpdate_AccountUnavailable";
        _userManager.FindByIdAsync(targetId.ToString()).Returns(new User { Id = targetId });
        _userEmailService.AddEmailAsync(targetId, email, Arg.Any<CancellationToken>(), _userId)
            .Returns(new AddEmailResult(Guid.Empty, string.Empty, false, errorKey));
        _userEmailService.AddVerifiedEmailAsync(targetId, email, Arg.Any<CancellationToken>(), _userId)
            .Returns(new UserEmailAddResult(Guid.Empty, false, false, errorKey));

        var result = verified
            ? await _controller.AdminAddVerifiedEmail(targetId, email, TestContext.Current.CancellationToken)
            : await _controller.AdminAddEmail(targetId, email, TestContext.Current.CancellationToken);

        result.Should().BeOfType<RedirectToActionResult>();
        _controller.TempData["ErrorMessage"].Should().Be(errorKey);
        _controller.TempData.Should().NotContainKey("SuccessMessage");
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdminEmailAddition_UnexpectedFailure_Propagates(bool verified)
    {
        var targetId = Guid.NewGuid();
        const string email = "added@example.com";
        _userManager.FindByIdAsync(targetId.ToString()).Returns(new User { Id = targetId });
        var failure = new InvalidOperationException("Dependency failed");
        _userEmailService.AddEmailAsync(targetId, email, Arg.Any<CancellationToken>(), _userId)
            .Returns(Task.FromException<AddEmailResult>(failure));
        _userEmailService.AddVerifiedEmailAsync(targetId, email, Arg.Any<CancellationToken>(), _userId)
            .Returns(Task.FromException<UserEmailAddResult>(failure));

        Func<Task<IActionResult>> act = () => verified
            ? _controller.AdminAddVerifiedEmail(targetId, email, TestContext.Current.CancellationToken)
            : _controller.AdminAddEmail(targetId, email, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        _controller.TempData.Should().NotContainKey("ErrorMessage");
        _controller.TempData.Should().NotContainKey("SuccessMessage");
    }

    [HumansFact]
    public async Task AdminUnlink_AsAdmin_CallsServiceWithRouteUserId()
    {
        var targetUserId = Guid.NewGuid();
        var emailId = Guid.NewGuid();
        _userEmailService.UnlinkAsync(targetUserId, emailId, _userId, Arg.Any<CancellationToken>())
            .Returns(new UnlinkEmailResult(true));

        var result = await _controller.AdminUnlink(targetUserId, emailId, TestContext.Current.CancellationToken);

        await _userEmailService.Received(1).UnlinkAsync(
            targetUserId, emailId, _userId, Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("AdminEmails");
    }

    [HumansFact]
    public async Task AdminDeleteEmail_AsAdmin_CallsServiceWithRouteUserId()
    {
        var targetUserId = Guid.NewGuid();
        var emailId = Guid.NewGuid();
        _userEmailService.DeleteEmailAsync(targetUserId, emailId, Arg.Any<CancellationToken>(), _userId)
            .Returns(new DeleteEmailResult(true));

        var result = await _controller.AdminDeleteEmail(targetUserId, emailId, TestContext.Current.CancellationToken);

        await _userEmailService.Received(1).DeleteEmailAsync(
            targetUserId, emailId, Arg.Any<CancellationToken>(), _userId);
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("AdminEmails");
    }

    [HumansFact]
    public async Task AdminSetVisibility_AsAdmin_CallsServiceWithRouteUserId()
    {
        var targetUserId = Guid.NewGuid();
        var emailId = Guid.NewGuid();

        var result = await _controller.AdminSetVisibility(
            targetUserId, emailId, ContactFieldVisibility.BoardOnly, TestContext.Current.CancellationToken);

        await _userEmailService.Received(1).SetVisibilityAsync(
            targetUserId, emailId, ContactFieldVisibility.BoardOnly, Arg.Any<CancellationToken>(), _userId);
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("AdminEmails");
    }

    // Self-path actions must authorize (UserEmailOperations.Edit) and audit, like the admin path.

    [HumansFact]
    public async Task DeleteEmail_AuthorizationFails_ReturnsForbid()
    {
        var emailId = Guid.NewGuid();
        _authorizationService.AuthorizeAsync(
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<object?>(),
            Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var result = await _controller.DeleteEmail(emailId);

        result.Should().BeOfType<ForbidResult>();
        await _userEmailService.DidNotReceive().DeleteEmailAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>(), _userId);
    }

    [HumansFact]
    public async Task SetEmailVisibility_AuthorizationFails_ReturnsForbid()
    {
        var emailId = Guid.NewGuid();
        _authorizationService.AuthorizeAsync(
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<object?>(),
            Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var result = await _controller.SetEmailVisibility(emailId, "BoardOnly");

        result.Should().BeOfType<ForbidResult>();
        await _userEmailService.DidNotReceive().SetVisibilityAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<ContactFieldVisibility?>(), Arg.Any<CancellationToken>(), _userId);
    }

    [HumansFact]
    public async Task SetPrimary_AsSelf_PassesUserAsActor()
    {
        var emailId = Guid.NewGuid();

        var result = await _controller.SetPrimary(emailId, TestContext.Current.CancellationToken);

        await _userEmailService.Received(1).SetPrimaryAsync(_userId, emailId, Arg.Any<CancellationToken>(), _userId);
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("Emails");
    }

    [HumansTheory]
    [InlineData("delete", false)]
    [InlineData("admin-delete", false)]
    [InlineData("unlink", false)]
    [InlineData("admin-unlink", false)]
    [InlineData("admin-delete", true)]
    [InlineData("admin-unlink", true)]
    [InlineData("primary", false)]
    [InlineData("primary", true)]
    [InlineData("admin-primary", false)]
    [InlineData("admin-primary", true)]
    [InlineData("visibility", false)]
    [InlineData("visibility", true)]
    [InlineData("admin-visibility", false)]
    [InlineData("admin-visibility", true)]
    [InlineData("clear", false)]
    [InlineData("clear", true)]
    [InlineData("admin-clear", false)]
    [InlineData("admin-clear", true)]
    public async Task EmailMutation_DependencyFailuresPropagateWithoutRefusalFeedback(string action, bool validation)
    {
        var row = Guid.NewGuid();
        Exception failure = validation ? new ValidationException("Private dependency diagnostic")
            : new InvalidOperationException("Private dependency diagnostic");
        _userEmailService.SetPrimaryAsync(_userId, row, Arg.Any<CancellationToken>(), _userId)
            .Returns(Task.FromException<UpdateEmailResult>(failure));
        _userEmailService.SetVisibilityAsync(_userId, row, Arg.Any<ContactFieldVisibility?>(), Arg.Any<CancellationToken>(), _userId)
            .Returns(Task.FromException<UpdateEmailResult>(failure));
        _userEmailService.ClearPrimaryAsync(_userId, row, _userId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(failure));
        _userEmailService.DeleteEmailAsync(_userId, row, Arg.Any<CancellationToken>(), _userId)
            .Returns(Task.FromException<DeleteEmailResult>(failure));
        _userEmailService.UnlinkAsync(_userId, row, _userId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<UnlinkEmailResult>(failure));
        Func<Task> act = async () =>
        {
            _ = action switch
            {
                "primary" => await _controller.SetPrimary(row, TestContext.Current.CancellationToken),
                "admin-primary" => await _controller.AdminSetPrimary(_userId, row, TestContext.Current.CancellationToken),
                "visibility" => await _controller.SetEmailVisibility(row, null),
                "admin-visibility" => await _controller.AdminSetVisibility(_userId, row, null, TestContext.Current.CancellationToken),
                "clear" => await _controller.ClearPrimary(row, TestContext.Current.CancellationToken),
                "admin-clear" => await _controller.AdminClearPrimary(_userId, row, TestContext.Current.CancellationToken),
                "delete" => await _controller.DeleteEmail(row),
                "admin-delete" => await _controller.AdminDeleteEmail(_userId, row, TestContext.Current.CancellationToken),
                "unlink" => await _controller.Unlink(row, TestContext.Current.CancellationToken),
                _ => await _controller.AdminUnlink(_userId, row, TestContext.Current.CancellationToken)
            };
        };

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
        _controller.TempData.Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData("Users_EmailUpdate_NotFound", false)]
    [InlineData("Users_EmailUpdate_NotFound", true)]
    [InlineData("Users_EmailGrid_DeleteRejectedHasProvider", false)]
    [InlineData("Users_EmailGrid_DeletePrimaryBlocked", false)]
    [InlineData("Users_EmailDelete_LastVerifiedBlocked", false)]
    [InlineData("Users_EmailGrid_DeleteTicketLinkedBlocked", false)]
    [InlineData("Users_EmailGrid_DeleteRejectedHasProvider", true)]
    [InlineData("Users_EmailGrid_DeletePrimaryBlocked", true)]
    [InlineData("Users_EmailDelete_LastVerifiedBlocked", true)]
    [InlineData("Users_EmailGrid_DeleteTicketLinkedBlocked", true)]
    public async Task DeleteEmail_ExpectedRefusal_IsLocalizedWithoutDeletionAudit(string errorKey, bool admin)
    {
        var emailId = Guid.NewGuid();
        _userEmailService.DeleteEmailAsync(_userId, emailId, Arg.Any<CancellationToken>(), _userId)
            .Returns(new DeleteEmailResult(false, errorKey));

        var result = admin
            ? await _controller.AdminDeleteEmail(_userId, emailId, TestContext.Current.CancellationToken)
            : await _controller.DeleteEmail(emailId);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(admin ? "AdminEmails" : "Emails");
        _controller.TempData["ErrorMessage"].Should().Be(errorKey);
    }

    [HumansFact]
    public async Task DeleteEmail_AsSelf_PassesUserAsActor()
    {
        var emailId = Guid.NewGuid();
        _userEmailService.DeleteEmailAsync(_userId, emailId, Arg.Any<CancellationToken>(), _userId).Returns(new DeleteEmailResult(true));

        var result = await _controller.DeleteEmail(emailId);

        await _userEmailService.Received(1).DeleteEmailAsync(_userId, emailId, Arg.Any<CancellationToken>(), _userId);
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("Emails");
    }

    [HumansTheory]
    [InlineData("99", null)]
    [InlineData("-1", null)]
    [InlineData("BoardOnly", ContactFieldVisibility.BoardOnly)]
    [InlineData("allactiveprofiles", ContactFieldVisibility.AllActiveProfiles)]
    [InlineData("0", ContactFieldVisibility.BoardOnly)]
    [InlineData("3", ContactFieldVisibility.AllActiveProfiles)]
    [InlineData("", null)]
    [InlineData("unsupported", null)]
    public async Task SetEmailVisibility_UsesDefinedVisibilityOrHidden(string input, ContactFieldVisibility? expected)
    {
        var emailId = Guid.NewGuid();

        var result = await _controller.SetEmailVisibility(emailId, input);

        result.Should().BeOfType<RedirectToActionResult>();
        await _userEmailService.Received(1).SetVisibilityAsync(
            _userId, emailId, expected, Arg.Any<CancellationToken>(), _userId);
    }

    [HumansFact]
    public async Task SetEmailVisibility_AsSelf_PassesUserAsActor()
    {
        var emailId = Guid.NewGuid();

        var result = await _controller.SetEmailVisibility(emailId, "BoardOnly");

        await _userEmailService.Received(1).SetVisibilityAsync(_userId, emailId, ContactFieldVisibility.BoardOnly, Arg.Any<CancellationToken>(), _userId);
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("Emails");
    }

    // Linked OAuth accounts: unlink is keyed by (Provider, ProviderKey), and is refused when no
    // verified UserEmail row would remain (that row is what keeps magic-link sign-in working).

    private static UserEmailRowSnapshot Snapshot(
        Guid userId, Guid id, string email, bool isVerified,
        string? provider, string? providerKey)
        => new(
            Id: id,
            UserId: userId,
            Email: email,
            IsVerified: isVerified,
            Provider: provider,
            ProviderKey: providerKey,
            IsPrimary: false,
            Visibility: null,
            VerificationSentAt: null,
            CreatedAt: Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt: Instant.FromUtc(2026, 1, 1, 0, 0));

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnlinkLinkedAccount_DelegatesProviderIdentityAndLocalizesResult(bool succeeds)
    {
        _userEmailService.UnlinkAsync(_userId, null, _userId, Arg.Any<CancellationToken>(), "Google", "key")
            .Returns(new UnlinkEmailResult(succeeds,
                succeeds ? null : "Users_LinkedAccounts_UnlinkBlockedLastSignInMethod"));

        var result = await _controller.UnlinkLinkedAccount("Google", "key", TestContext.Current.CancellationToken);

        await _userEmailService.Received(1).UnlinkAsync(
            _userId, null, _userId, Arg.Any<CancellationToken>(), "Google", "key");
        _controller.TempData[succeeds ? "SuccessMessage" : "ErrorMessage"].Should().Be(
            succeeds ? "Users_EmailGrid_UnlinkSuccess" : "Users_LinkedAccounts_UnlinkBlockedLastSignInMethod");
        await _userManager.DidNotReceive().RemoveLoginAsync(Arg.Any<User>(), Arg.Any<string>(), Arg.Any<string>());
        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Emails");
    }

    [HumansFact]
    public async Task UnlinkLinkedAccount_AuthorizationFails_ReturnsForbid()
    {
        _authorizationService.AuthorizeAsync(
            Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var result = await _controller.UnlinkLinkedAccount("Google", "key", TestContext.Current.CancellationToken);

        result.Should().BeOfType<ForbidResult>();
        await _userEmailService.DidNotReceive().UnlinkAsync(
            Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>(),
            Arg.Any<string?>(), Arg.Any<string?>());
    }
}
