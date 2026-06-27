using System.Security.Claims;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using Humans.Web.Models.OnboardingWidget;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Controllers;

public class OnboardingWidgetControllerNamesTests
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

    public OnboardingWidgetControllerNamesTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);
        _localizer[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
    }

    private OnboardingWidgetController BuildSut(Guid userId, string lang = "en", OnboardingWidgetStep currentStep = OnboardingWidgetStep.Names)
    {
        var user = new User { Id = userId };
        _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(user);
        _state.GetCurrentStepAsync(userId, Arg.Any<CancellationToken>()).Returns(currentStep);
        var ctrl = new OnboardingWidgetController(_userService, _state, _profileEditor, _signups, _shiftMgmt, _shiftView, _consents, _onboardingService, SystemClock.Instance, _localizer);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                "test")),
        };
        http.Request.Headers["Accept-Language"] = lang;
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctrl;
    }

    [HumansFact]
    public async Task Names_Get_ReturnsBlankViewModel_IgnoringOauthClaims()
    {
        // OAuth-supplied legal names are unverified — provider-set GivenName /
        // Surname must NOT prefill the form. Reports of users blowing through
        // the Names step with whatever Google handed us are the regression
        // this guards. The prefill reads the saved profile (none here → blank),
        // never the claims.
        var userId = Guid.NewGuid();
        var ctrl = new OnboardingWidgetController(
            _userService, _state, _profileEditor, _signups, _shiftMgmt, _shiftView, _consents, _onboardingService, SystemClock.Instance, _localizer);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.GivenName, "OauthFirst"),
                new Claim(ClaimTypes.Surname, "OauthLast"),
            ], "test")),
        };
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };

        var result = await ctrl.Names(TestContext.Current.CancellationToken);

        var view = Assert.IsType<ViewResult>(result);
        var vm = Assert.IsType<NamesViewModel>(view.Model);
        Assert.Equal(string.Empty, vm.BurnerName);
        Assert.Equal(string.Empty, vm.FirstName);
        Assert.Equal(string.Empty, vm.LastName);
    }

    [HumansFact]
    public async Task Names_Get_PrefillsFromSavedProfile()
    {
        // A returning or data-drifted user must see their saved name fields, not a
        // blank form that looks like their earlier entry never persisted.
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(MakeUserInfo(userId, burner: "Saved", first: "Jean", last: "Dupont"));
        var ctrl = BuildSut(userId);

        var result = await ctrl.Names(TestContext.Current.CancellationToken);

        var view = Assert.IsType<ViewResult>(result);
        var vm = Assert.IsType<NamesViewModel>(view.Model);
        Assert.Equal("Saved", vm.BurnerName);
        Assert.Equal("Jean", vm.FirstName);
        Assert.Equal("Dupont", vm.LastName);
    }

    [HumansFact]
    public async Task Names_Post_SavesProfile_AndRedirectsToShifts()
    {
        var userId = Guid.NewGuid();
        _profileEditor.SaveProfileAsync(
                userId,
                "Burner1",
                Arg.Any<ProfileSaveRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());

        var ctrl = BuildSut(userId);
        var vm = new NamesViewModel { BurnerName = "Burner1", FirstName = "First", LastName = "Last" };

        var result = await ctrl.Names(vm, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Shifts), redirect.ActionName);

        await _profileEditor.Received(1).SaveProfileAsync(
            userId,
            "Burner1",
            Arg.Is<ProfileSaveRequest>(r =>
                r.FirstName == "First" &&
                r.LastName == "Last" &&
                r.BurnerName == "Burner1"),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Names_Post_TriggersOnboardingConsentCheck_AfterSave()
    {
        // The consent-check trigger is a controller peer-call after SaveProfileAsync,
        // per no-leaf-to-director-callbacks — not inside the service.
        var userId = Guid.NewGuid();
        _profileEditor.SaveProfileAsync(
                userId, Arg.Any<string>(), Arg.Any<ProfileSaveRequest>(), Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());

        var ctrl = BuildSut(userId);
        var vm = new NamesViewModel { BurnerName = "Burner1", FirstName = "First", LastName = "Last" };

        await ctrl.Names(vm, TestContext.Current.CancellationToken);

        await _onboardingService.Received(1)
            .SetConsentCheckPendingIfEligibleAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Names_Post_RedirectsToIndex_AndDoesNotSave_WhenUserAlreadyNamed()
    {
        // Names POST is reachable directly. SaveProfileAsync does a full
        // overwrite, so re-posting with a Names-only viewmodel would clobber
        // existing City/Bio/EmergencyContact/etc. Guard: bail out when the user
        // already has their name on file — an in-section check against their own
        // profile, NOT the consent/step state.
        var userId = Guid.NewGuid();
        var ctrl = BuildSut(userId);
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(MakeUserInfo(userId, burner: "Existing", first: "Already", last: "Named"));
        var vm = new NamesViewModel { BurnerName = "Burner1", FirstName = "First", LastName = "Last" };

        var result = await ctrl.Names(vm, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Index), redirect.ActionName);

        await _profileEditor.DidNotReceiveWithAnyArgs().SaveProfileAsync(
            Guid.Empty, null!, null!, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Names_Post_SavesProfile_EvenWhenConsentsVacuouslyComplete()
    {
        // Regression for the OnboardingWidget/Names redirect loop: when the
        // Volunteers team has no required+effective legal docs the dispatcher's
        // step resolves to Complete vacuously. The name save must NOT be gated by
        // that cross-section state — a bare account must still be able to save its
        // name and move on, or it loops on Names forever (Names POST → Index →
        // Home → NameRequiredFilter → Names, save skipped, nothing logged).
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(MakeUserInfo(userId, burner: "", first: "", last: "")); // bare account, no name yet
        // Step resolves to Complete (vacuous consents) — the POST must ignore it.
        var ctrl = BuildSut(userId, currentStep: OnboardingWidgetStep.Complete);
        var vm = new NamesViewModel { BurnerName = "Burner1", FirstName = "First", LastName = "Last" };

        var result = await ctrl.Names(vm, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Shifts), redirect.ActionName);

        await _profileEditor.Received(1).SaveProfileAsync(
            userId,
            "Burner1",
            Arg.Is<ProfileSaveRequest>(r =>
                r.FirstName == "First" && r.LastName == "Last" && r.BurnerName == "Burner1"),
            Arg.Any<CancellationToken>());
    }

    private static UserInfo MakeUserInfo(
        Guid userId, string burner, string first, string last,
        string? city = null, string? bio = null) =>
        UserInfo.Create(
            user: new User
            {
                Id = userId,
                DisplayName = burner,
                PreferredLanguage = "en",
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            },
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: new Profile
            {
                UserId = userId,
                BurnerName = burner,
                FirstName = first,
                LastName = last,
                City = city,
                Bio = bio,
                State = string.IsNullOrWhiteSpace(burner) || string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(last)
                    ? ProfileState.Stub
                    : ProfileState.Active,
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            },
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);

    [HumansFact]
    public async Task Names_Post_PreservesExistingProfileFields_WhenFixingDriftedName()
    {
        // A data-drifted profile (City/Bio on file but a blank required name) reaches
        // this POST via NameRequiredFilter. SaveProfileAsync is a full-field overwrite,
        // so fixing the name must carry the existing City/Bio forward, not null them.
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(MakeUserInfo(userId, burner: "Drifted", first: "Has", last: "", city: "Madrid", bio: "existing bio"));
        var ctrl = BuildSut(userId);
        var vm = new NamesViewModel { BurnerName = "Drifted", FirstName = "Has", LastName = "NowSet" };

        var result = await ctrl.Names(vm, TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Shifts), redirect.ActionName);

        await _profileEditor.Received(1).SaveProfileAsync(
            userId,
            "Drifted",
            Arg.Is<ProfileSaveRequest>(r =>
                r.LastName == "NowSet" &&
                r.City == "Madrid" &&
                r.Bio == "existing bio"),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Names_Post_InvalidModel_ReturnsView()
    {
        var ctrl = BuildSut(Guid.NewGuid());
        ctrl.ModelState.AddModelError(nameof(NamesViewModel.BurnerName), "required");

        var result = await ctrl.Names(new NamesViewModel(), TestContext.Current.CancellationToken);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Names), view.ViewName ?? nameof(OnboardingWidgetController.Names));

        await _profileEditor.DidNotReceiveWithAnyArgs().SaveProfileAsync(
            Guid.Empty, null!, null!, Arg.Any<CancellationToken>());
    }
}
