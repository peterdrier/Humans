using System.Security.Claims;
using Humans.Consent;
using Humans.Consent.Contracts;
using Humans.Onboarding.Services;
using Humans.Users.Contracts;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Base.Constants;
using Humans.Onboarding.Controllers;
using Humans.Onboarding.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Onboarding.Tests.Controllers;

/// <summary>
/// Step 3 of the onboarding widget — Consents GET picks the next unsigned required document;
/// Consent's own signing step renders it and takes the signature.
/// </summary>
public class OnboardingWidgetControllerConsentsTests
{
    private readonly IOnboardingWidgetState _state = Substitute.For<IOnboardingWidgetState>();
    private readonly IProfileEditorService _profileEditor = Substitute.For<IProfileEditorService>();
    private readonly IShiftSignups _signups = Substitute.For<IShiftSignups>();
    private readonly IShiftService _shiftMgmt = Substitute.For<IShiftService>();
    private readonly ISettingsService _burnSettings = Substitute.For<ISettingsService>();
    private readonly IShiftView _shiftView = Substitute.For<IShiftView>();
    private readonly IOnboardingService _onboardingService = Substitute.For<IOnboardingService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();

    private readonly IStringLocalizer<OnboardingResource> _localizer =
        Substitute.For<IStringLocalizer<OnboardingResource>>();
    private readonly DefaultHttpContext _http = new();

    public OnboardingWidgetControllerConsentsTests()
    {
        _localizer[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
    }

    private OnboardingWidgetController BuildSut(Guid userId, bool isStub = false)
    {
        _http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            "test"));
        // Default: user has a non-Stub profile so the pre-flight gate
        // doesn't divert tests that exercise the consent flow itself.
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(isStub ? StubUserInfo(userId) : NonStubUserInfo(userId));
        var ctrl = new OnboardingWidgetController(_userService, _state, _profileEditor, _signups, _shiftMgmt, _burnSettings, _shiftView, _onboardingService, SystemClock.Instance, _localizer, Microsoft.Extensions.Logging.Abstractions.NullLogger<OnboardingWidgetController>.Instance);
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = _http,
        };
        ctrl.TempData = new TempDataDictionary(_http, Substitute.For<ITempDataProvider>());
        // Pre-set Url so RedirectToAction doesn't try to resolve IUrlHelperFactory from RequestServices.
        ctrl.Url = Substitute.For<IUrlHelper>();
        return ctrl;
    }

    private static UserInfo NonStubUserInfo(Guid userId) => WrapInUserInfo(
        userId,
        UserFixtures.Profile(
            burnerName: "Burner",
            firstName: "First",
            lastName: "Last"));

    private static UserInfo StubUserInfo(Guid userId) => WrapInUserInfo(
        userId,
        UserFixtures.Profile());

    private static UserInfo WrapInUserInfo(Guid userId, ProfileInfo profile) => UserInfo.Create(
        user: new User
        {
            Id = userId,
            DisplayName = "Test",
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            State = UserFixtures.StateFor(profile),
        },
        userEmails: [],
        externalLogins: [],
        profile: profile);

    [HumansFact]
    public async Task Consents_Get_StubProfile_RedirectsToNames_WithLocalizedInfo()
    {
        // A nameless user at the consent step would only get a refused form: bounce them to Names.
        var userId = Guid.NewGuid();
        var ctrl = BuildSut(userId, isStub: true);

        var result = await ctrl.Consents(TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Names), redirect.ActionName);
        Assert.Equal("Onboarding_StubProfile_AddName", ctrl.TempData[TempDataKeys.InfoMessage]);
        await _onboardingService.DidNotReceive().GetNextUnsignedConsentAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Consents_Get_RendersFirstUnsignedDocumentContent()
    {
        // The widget shows the next unsigned doc inline (full content) so users
        // can read what they're agreeing to — not just a "Read the document"
        // link they might skip.
        var userId = Guid.NewGuid();
        var unsignedId = Guid.NewGuid();
        _onboardingService.GetNextUnsignedConsentAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new NextConsentStepData(
                NextConsentStepOutcome.Document,
                new ConsentReviewDetail(
                    unsignedId,
                    "Privacy Policy",
                    "1.2",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "# Politica", ["en"] = "# Policy" },
                    Instant.FromUnixTimeSeconds(0),
                    "Updated section 4",
                    HasAlreadyConsented: false,
                    ConsentedAt: null,
                    UserFullName: null),
                CurrentIndex: 2,
                TotalRequired: 2));
        var ctrl = BuildSut(userId);

        var result = await ctrl.Consents(TestContext.Current.CancellationToken);

        var view = Assert.IsType<ViewResult>(result);
        var vm = Assert.IsType<ConsentsStepViewModel>(view.Model);
        Assert.Equal(unsignedId, vm.Document.DocumentVersionId);
        Assert.Equal("Privacy Policy", vm.Document.DocumentName);
        Assert.Equal(2, vm.CurrentIndex);   // signed (1), unsigned (2)
        Assert.Equal(2, vm.TotalRequired);
    }

    [HumansFact]
    public async Task Consents_Get_NothingLeftToSign_RedirectsThroughIndex()
    {
        // Nothing left to sign → back through the Index dispatcher. The consent-suspension
        // self-heal for this case lives in IOnboardingService.GetNextUnsignedConsentAsync
        // (see OnboardingServiceTests), not in the controller.
        var userId = Guid.NewGuid();
        _onboardingService.GetNextUnsignedConsentAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new NextConsentStepData(NextConsentStepOutcome.NothingLeftToSign, null, 0, 1));
        var ctrl = BuildSut(userId);

        var result = await ctrl.Consents(TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Index), redirect.ActionName);
    }

    [HumansFact]
    public async Task Consents_Get_DocumentUnavailable_StopsInsteadOfBouncingOffTheDispatcher()
    {
        // Handing an unloadable unsigned document back to the dispatcher loops: it still answers "consents".
        var userId = Guid.NewGuid();
        _onboardingService.GetNextUnsignedConsentAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new NextConsentStepData(NextConsentStepOutcome.DocumentUnavailable, null, 0, 1));
        var ctrl = BuildSut(userId);

        var result = await ctrl.Consents(TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Home", redirect.ControllerName);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal(
            "Onboarding_ConsentDocumentUnavailable",
            ctrl.TempData["ErrorMessage"]);
    }
}
