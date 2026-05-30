using System.Security.Claims;
using Humans.Application;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Constants;
using Humans.Web.Controllers;
using Humans.Web.Models.OnboardingWidget;
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
/// Step 3 of the onboarding widget — Consents GET (renders the next unsigned
/// required document inline so the user reads what they're agreeing to) and
/// SignConsent POST (routes a single signature through
/// <see cref="IConsentService.SubmitConsentAsync"/> and redirects back so the
/// dispatcher can re-evaluate the step).
/// </summary>
public class OnboardingWidgetControllerConsentsTests
{
    private readonly UserManager<User> _userManager;
    private readonly IOnboardingWidgetState _state = Substitute.For<IOnboardingWidgetState>();
    private readonly IProfileEditorService _profileEditor = Substitute.For<IProfileEditorService>();
    private readonly IShiftSignupService _signups = Substitute.For<IShiftSignupService>();
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly IShiftView _shiftView = Substitute.For<IShiftView>();
    private readonly IConsentService _consents = Substitute.For<IConsentService>();
    private readonly IOnboardingService _onboardingService = Substitute.For<IOnboardingService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IStringLocalizer<SharedResource> _localizer =
        Substitute.For<IStringLocalizer<SharedResource>>();
    private readonly DefaultHttpContext _http = new();

    public OnboardingWidgetControllerConsentsTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);
        _localizer[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
    }

    private OnboardingWidgetController BuildSut(Guid userId, bool isStub = false)
    {
        var user = new User { Id = userId };
        _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(user);
        _http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            "test"));
        // SetError on HumansControllerBase resolves ILoggerFactory from RequestServices.
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        _http.RequestServices = services.BuildServiceProvider();
        // Default: user has a non-Stub profile so the new pre-flight gate
        // doesn't divert tests that exercise the consent flow itself.
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(isStub ? StubUserInfo(userId) : NonStubUserInfo(userId));
        var ctrl = new OnboardingWidgetController(_userService, _state, _profileEditor, _signups, _shiftMgmt, _shiftView, _consents, _onboardingService, _localizer);
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = _http,
            ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor { ActionName = "Test" },
        };
        ctrl.TempData = new TempDataDictionary(_http, Substitute.For<ITempDataProvider>());
        // Pre-set Url so RedirectToAction doesn't try to resolve IUrlHelperFactory from RequestServices.
        ctrl.Url = Substitute.For<IUrlHelper>();
        return ctrl;
    }

    private static UserInfo NonStubUserInfo(Guid userId) => WrapInUserInfo(userId, new Profile
    {
        UserId = userId,
        BurnerName = "Burner",
        FirstName = "First",
        LastName = "Last",
        State = ProfileState.Active,
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
    });

    private static UserInfo StubUserInfo(Guid userId) => WrapInUserInfo(userId, new Profile
    {
        UserId = userId,
        BurnerName = "",
        FirstName = "",
        LastName = "",
        State = ProfileState.Stub,
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
    });

    private static UserInfo WrapInUserInfo(Guid userId, Profile profile) => UserInfo.Create(
        user: new User
        {
            Id = userId,
            DisplayName = "Test",
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
    public async Task SignConsent_Post_CallsConsentService_AndRedirectsThroughIndexDispatcher()
    {
        // Routing back through Index lets the dispatcher send the user Home
        // when this was the final required consent — instead of stranding
        // them on the signed-documents page.
        var userId = Guid.NewGuid();
        var docVersionId = Guid.NewGuid();
        _consents.SubmitConsentAsync(
                userId, docVersionId, true,
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConsentSubmitResult(Success: true));
        var ctrl = BuildSut(userId);

        var result = await ctrl.SignConsent(docVersionId, explicitConsent: true, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Index), redirect.ActionName);
        await _consents.Received(1).SubmitConsentAsync(
            userId, docVersionId, true,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SignConsent_Post_AlreadyConsented_RedirectsToIndex_WithLocalizedInfo()
    {
        // AlreadyConsented isn't really an error — they already signed it.
        // Mirror ConsentController: localized info, never the raw error key.
        var userId = Guid.NewGuid();
        var docVersionId = Guid.NewGuid();
        _consents.SubmitConsentAsync(
                userId, docVersionId, true,
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConsentSubmitResult(Success: false, ErrorKey: "AlreadyConsented"));
        var ctrl = BuildSut(userId);

        var result = await ctrl.SignConsent(docVersionId, explicitConsent: true, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Index), redirect.ActionName);
        // Localizer stub returns the key as the value — verifies localization
        // ran rather than the raw ErrorKey being dumped into TempData.
        Assert.Equal("Consent_AlreadyConsented", ctrl.TempData[TempDataKeys.InfoMessage]);
        Assert.Null(ctrl.TempData[TempDataKeys.ErrorMessage]);
    }

    [HumansFact]
    public async Task Consents_Get_StubProfile_RedirectsToNames_WithLocalizedInfo()
    {
        // Stub-profile gate: a user with no legal name reaching the consent
        // step (the original "StubProfile red toast" production bug) gets
        // bounced back to the Names step with a localized prompt rather than
        // the doomed-form / raw-error-key UX.
        var userId = Guid.NewGuid();
        var ctrl = BuildSut(userId, isStub: true);

        var result = await ctrl.Consents(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Names), redirect.ActionName);
        Assert.Equal("Consent_StubProfile_AddName", ctrl.TempData[TempDataKeys.InfoMessage]);
        await _consents.DidNotReceive().GetRequiredConsentRowsForUserAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SignConsent_Post_StubProfile_RedirectsToNames_AndDoesNotCallService()
    {
        var userId = Guid.NewGuid();
        var ctrl = BuildSut(userId, isStub: true);

        var result = await ctrl.SignConsent(Guid.NewGuid(), explicitConsent: true, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Names), redirect.ActionName);
        Assert.Equal("Consent_StubProfile_AddName", ctrl.TempData[TempDataKeys.InfoMessage]);
        await _consents.DidNotReceive().SubmitConsentAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<bool>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SignConsent_Post_StubProfileErrorKey_RedirectsToNames_NoRawKeyDisplayed()
    {
        // Defense-in-depth: if upstream gates somehow miss and the service
        // returns ErrorKey "StubProfile", controller still must NOT display
        // the raw key. The localized info banner replaces it.
        var userId = Guid.NewGuid();
        var docVersionId = Guid.NewGuid();
        _consents.SubmitConsentAsync(
                userId, docVersionId, true,
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConsentSubmitResult(Success: false, ErrorKey: "StubProfile"));
        // BuildSut defaults to non-Stub UserInfo so the GET-side gate passes —
        // we want to specifically exercise the SubmitConsentAsync return path.
        var ctrl = BuildSut(userId);

        var result = await ctrl.SignConsent(docVersionId, explicitConsent: true, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Names), redirect.ActionName);
        Assert.Equal("Consent_StubProfile_AddName", ctrl.TempData[TempDataKeys.InfoMessage]);
        Assert.Null(ctrl.TempData[TempDataKeys.ErrorMessage]);
    }

    [HumansFact]
    public async Task SignConsent_Post_WithoutCheckbox_RedirectsToConsents_WithError_AndDoesNotCallService()
    {
        // The checkbox is the legal "explicit consent" gesture — submitting
        // without it is a user error, not a service call. Route back to the
        // consent page with an error message; never invoke the consent service.
        var userId = Guid.NewGuid();
        var docVersionId = Guid.NewGuid();
        var ctrl = BuildSut(userId);

        var result = await ctrl.SignConsent(docVersionId, explicitConsent: false, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Consents), redirect.ActionName);
        // Localizer stub returns the key as the value, so this verifies that
        // the controller now goes through `_localizer["Consent_MustCheck"]`
        // (matches ConsentController.Submit) rather than emitting the raw key.
        Assert.Equal("Consent_MustCheck", ctrl.TempData[TempDataKeys.ErrorMessage]);
        await _consents.DidNotReceive().SubmitConsentAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<bool>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Consents_Get_RendersFirstUnsignedDocumentContent()
    {
        // The widget shows the next unsigned doc inline (full content) so users
        // can read what they're agreeing to — not just a "Read the document"
        // link they might skip.
        var userId = Guid.NewGuid();
        var signedId = Guid.NewGuid();
        var unsignedId = Guid.NewGuid();
        IReadOnlyList<RequiredConsentRow> rows = new List<RequiredConsentRow>
        {
            new(signedId, "Code of Conduct", Signed: true),
            new(unsignedId, "Privacy Policy", Signed: false),
        };
        _consents.GetRequiredConsentRowsForUserAsync(
                userId, SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(rows); _consents.GetConsentReviewDetailAsync(unsignedId, userId, Arg.Any<CancellationToken>())
            .Returns(new ConsentReviewDetail(
                unsignedId,
                "Privacy Policy",
                "1.2",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "# Politica", ["en"] = "# Policy" },
                Instant.FromUnixTimeSeconds(0),
                "Updated section 4",
                HasAlreadyConsented: false,
                ConsentedAt: null,
                UserFullName: null));
        var ctrl = BuildSut(userId);

        var result = await ctrl.Consents(CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        var vm = Assert.IsType<ConsentsStepViewModel>(view.Model);
        Assert.Equal(unsignedId, vm.DocumentVersionId);
        Assert.Equal("Privacy Policy", vm.DocumentName);
        Assert.Equal("1.2", vm.VersionNumber);
        Assert.Equal("Updated section 4", vm.ChangesSummary);
        Assert.Equal(2, vm.CurrentIndex);   // signed (1), unsigned (2)
        Assert.Equal(2, vm.TotalRequired);
        Assert.Equal("# Policy", vm.Content["en"]);
    }

    [HumansFact]
    public async Task Consents_Get_AllSigned_RedirectsThroughIndexDispatcher()
    {
        var userId = Guid.NewGuid();
        IReadOnlyList<RequiredConsentRow> rows = new List<RequiredConsentRow>
        {
            new(Guid.NewGuid(), "Code of Conduct", Signed: true),
        };
        _consents.GetRequiredConsentRowsForUserAsync(
                userId, SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(rows);
        var ctrl = BuildSut(userId);

        var result = await ctrl.Consents(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Index), redirect.ActionName);
    }
}
