using System.Security.Claims;
using Humans.Application;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Gates: <c>Review</c> (GET) and <c>Submit</c> (POST) redirect Stub-state
/// profiles (null legal name) to <c>/Profile/Edit</c> rather than rendering
/// the signing form or persisting a <c>ConsentRecord</c>.
/// </summary>
public class ConsentControllerTests
{
    private readonly UserManager<User> _userManager;
    private readonly IConsentService _consentService = Substitute.For<IConsentService>();
    private readonly IOnboardingService _onboardingService = Substitute.For<IOnboardingService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IStringLocalizer<SharedResource> _localizer =
        Substitute.For<IStringLocalizer<SharedResource>>();
    private readonly DefaultHttpContext _http = new();

    public ConsentControllerTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);
        _localizer[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
    }

    private ConsentController BuildSut(Guid userId)
    {
        var user = new User { Id = userId };
        _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(user);
        _http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            "test"));
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        _http.RequestServices = services.BuildServiceProvider();
        _http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("127.0.0.1");

        var ctrl = new ConsentController(
            _userService,
            _consentService,
            _onboardingService,
            _localizer,
            NullLogger<ConsentController>.Instance);
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = _http,
            ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor { ActionName = "Test" },
        };
        ctrl.TempData = new TempDataDictionary(_http, Substitute.For<ITempDataProvider>());
        ctrl.Url = Substitute.For<IUrlHelper>();
        return ctrl;
    }

    private static Profile StubProfile(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        BurnerName = "",
        FirstName = "",
        LastName = "",
        State = ProfileState.Stub,
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
    };

    private static Profile ActiveProfile(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        BurnerName = "Burner",
        FirstName = "First",
        LastName = "Last",
        State = ProfileState.Active,
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
    };

    private static UserInfo WrapInUserInfo(Profile profile) => UserInfo.Create(
        user: new User
        {
            Id = profile.UserId,
            DisplayName = profile.BurnerName,
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            GoogleEmailStatus = GoogleEmailStatus.Unknown,
        },
        userEmails: [],
        eventParticipations: [],
        externalLogins: [],
        profile: profile,
        contactFields: [],
        profileLanguages: [],
        volunteerHistory: [],
        communicationPreferences: []);

    [HumansFact]
    public async Task Review_Get_StubProfile_RedirectsToProfileEdit()
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(StubProfile(userId)));
        var ctrl = BuildSut(userId);

        var result = await ctrl.Review(Guid.NewGuid());

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Edit", redirect.ActionName);
        Assert.Equal("Profile", redirect.ControllerName);
        // Service must NOT be called when gated.
        await _consentService.DidNotReceive().GetConsentReviewDetailAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Submit_Post_StubProfile_RedirectsToProfileEdit_AndDoesNotSubmit()
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(StubProfile(userId)));
        var ctrl = BuildSut(userId);

        var result = await ctrl.Submit(new Models.ConsentSubmitModel
        {
            DocumentVersionId = Guid.NewGuid(),
            ExplicitConsent = true,
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Edit", redirect.ActionName);
        Assert.Equal("Profile", redirect.ControllerName);
        await _consentService.DidNotReceive().SubmitConsentAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<bool>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Review_Get_ActiveProfile_DoesNotRedirect()
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(ActiveProfile(userId)));
        var documentVersionId = Guid.NewGuid();
        // Service returns nothing — controller will NotFound. The point of
        // the test is that the Stub redirect was NOT taken; a NotFound is
        // proof that control reached the service.
        _consentService.GetConsentReviewDetailAsync(
                documentVersionId, userId, Arg.Any<CancellationToken>())
            .Returns((ConsentReviewDetail?)null);
        var ctrl = BuildSut(userId);

        var result = await ctrl.Review(documentVersionId);

        Assert.IsType<NotFoundResult>(result);
        await _consentService.Received(1).GetConsentReviewDetailAsync(
            documentVersionId, userId, Arg.Any<CancellationToken>());
    }
}
