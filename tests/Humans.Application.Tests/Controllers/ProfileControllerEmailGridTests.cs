using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Authorization.UserEmail;
using Humans.Application.Configuration;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Testing;
using Humans.Web;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Controllers;

/// <summary>
/// Unit tests for the self-route controller actions on
/// <see cref="ProfileController"/> that wrap the new UserEmailService grid
/// methods (SetGoogle, Link, Unlink) and the SetPrimary rename. Covers PR 4
/// tasks 13/14/15 of the email/OAuth decoupling plan.
/// </summary>
public class ProfileControllerEmailGridTests
{
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IAuthorizationService _authorizationService = Substitute.For<IAuthorizationService>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly UserManager<User> _userManager;
    private readonly SignInManager<User> _signInManager;
    private readonly ProfileController _controller;
    private readonly Guid _userId = Guid.NewGuid();

    public ProfileControllerEmailGridTests()
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

        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));

        _controller = new ProfileController(
            _userManager,
            Substitute.For<IProfileService>(),
            Substitute.For<IContactFieldService>(),
            Substitute.For<IEmailService>(),
            _userEmailService,
            Substitute.For<ICommunicationPreferenceService>(),
            Substitute.For<IAuditLogService>(),
            Substitute.For<IOnboardingService>(),
            Substitute.For<IRoleAssignmentService>(),
            Substitute.For<IShiftSignupService>(),
            Substitute.For<IShiftManagementService>(),
            Substitute.For<IGdprExportService>(),
            Substitute.For<IConfiguration>(),
            new ConfigurationRegistry(),
            NullLogger<ProfileController>.Instance,
            localizer,
            Substitute.For<ITicketQueryService>(),
            Substitute.For<ITeamService>(),
            Substitute.For<ICampaignService>(),
            Substitute.For<IEmailOutboxService>(),
            _cache,
            new FakeClock(Instant.FromUtc(2026, 4, 30, 12, 0)),
            _authorizationService,
            Substitute.For<IUserService>(),
            Substitute.For<IHttpClientFactory>(),
            _signInManager);

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _userId.ToString()),
        }, authenticationType: "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = principal };
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        _controller.TempData = new TempDataDictionary(httpContext, Substitute.For<ITempDataProvider>());
        _controller.Url = Substitute.For<IUrlHelper>();
        _controller.Url.Action(Arg.Any<UrlActionContext>()).Returns("/Profile/Me/Emails");

        _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>())
            .Returns(new User { Id = _userId });

        _authorizationService.AuthorizeAsync(
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<object?>(),
            Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());
    }

    [HumansFact]
    public async Task SetGoogle_AsSelf_CallsSetGoogleAsync_AndRedirectsToGrid()
    {
        var emailId = Guid.NewGuid();
        _userEmailService.SetGoogleAsync(_userId, emailId, _userId, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _controller.SetGoogle(emailId, CancellationToken.None);

        await _userEmailService.Received(1).SetGoogleAsync(
            _userId, emailId, _userId, Arg.Any<CancellationToken>());
        result.Should().BeOfType<RedirectToActionResult>()
            .Which.ActionName.Should().Be("Emails");
    }

    [HumansFact]
    public async Task SetGoogle_AuthorizationFails_ReturnsForbid()
    {
        var emailId = Guid.NewGuid();
        _authorizationService.AuthorizeAsync(
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<object?>(),
            Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var result = await _controller.SetGoogle(emailId, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        await _userEmailService.DidNotReceive().SetGoogleAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Link_AsSelf_ReturnsChallengeResult_WithProvider()
    {
        _signInManager.ConfigureExternalAuthenticationProperties("Google", Arg.Any<string>())
            .Returns(new AuthenticationProperties());

        var result = await _controller.Link("Google", returnUrl: "/Profile/Me/Emails");

        result.Should().BeOfType<ChallengeResult>()
            .Which.AuthenticationSchemes.Should().Contain("Google");
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
    public async Task Unlink_AsSelf_CallsUnlinkAsync_AndRedirectsToGrid()
    {
        var emailId = Guid.NewGuid();
        _userEmailService.UnlinkAsync(_userId, emailId, _userId, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _controller.Unlink(emailId, CancellationToken.None);

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

        var result = await _controller.Unlink(emailId, CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        await _userEmailService.DidNotReceive().UnlinkAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
