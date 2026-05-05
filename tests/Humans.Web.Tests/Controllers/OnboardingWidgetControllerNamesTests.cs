using System.Security.Claims;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;
using Humans.Testing;
using Humans.Web.Controllers;
using Humans.Web.Models.OnboardingWidget;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Controllers;

public class OnboardingWidgetControllerNamesTests
{
    private readonly UserManager<User> _userManager;
    private readonly IOnboardingWidgetState _state = Substitute.For<IOnboardingWidgetState>();
    private readonly IProfileService _profile = Substitute.For<IProfileService>();
    private readonly IShiftSignupService _signups = Substitute.For<IShiftSignupService>();
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly IConsentService _consents = Substitute.For<IConsentService>();
    private readonly IStringLocalizer<Humans.Web.SharedResource> _localizer =
        Substitute.For<IStringLocalizer<Humans.Web.SharedResource>>();

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
        var ctrl = new OnboardingWidgetController(_userManager, _state, _profile, _signups, _shiftMgmt, _consents, _localizer);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) },
                "test")),
        };
        http.Request.Headers["Accept-Language"] = lang;
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctrl;
    }

    [HumansFact]
    public async Task Names_Post_SavesProfile_AndRedirectsToShifts()
    {
        var userId = Guid.NewGuid();
        _profile.SaveProfileAsync(
                userId,
                "Burner1",
                Arg.Any<ProfileSaveRequest>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());

        var ctrl = BuildSut(userId);
        var vm = new NamesViewModel { BurnerName = "Burner1", FirstName = "First", LastName = "Last" };

        var result = await ctrl.Names(vm, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Shifts), redirect.ActionName);

        await _profile.Received(1).SaveProfileAsync(
            userId,
            "Burner1",
            Arg.Is<ProfileSaveRequest>(r =>
                r.FirstName == "First" &&
                r.LastName == "Last" &&
                r.BurnerName == "Burner1"),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Names_Post_RedirectsToIndex_AndDoesNotSave_WhenAlreadyPastNamesStep()
    {
        // Names POST is reachable directly. SaveProfileAsync does a full
        // overwrite, so re-posting with a Names-only viewmodel would clobber
        // existing City/Bio/EmergencyContact/etc. Guard: bail out when the
        // user is past the Names step.
        var userId = Guid.NewGuid();
        var ctrl = BuildSut(userId, currentStep: OnboardingWidgetStep.Shifts);
        var vm = new NamesViewModel { BurnerName = "Burner1", FirstName = "First", LastName = "Last" };

        var result = await ctrl.Names(vm, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Index), redirect.ActionName);

        await _profile.DidNotReceiveWithAnyArgs().SaveProfileAsync(
            default, default!, default!, default!, default);
    }

    [HumansFact]
    public async Task Names_Post_InvalidModel_ReturnsView()
    {
        var ctrl = BuildSut(Guid.NewGuid());
        ctrl.ModelState.AddModelError(nameof(NamesViewModel.BurnerName), "required");

        var result = await ctrl.Names(new NamesViewModel(), CancellationToken.None);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Names), view.ViewName ?? nameof(OnboardingWidgetController.Names));

        await _profile.DidNotReceiveWithAnyArgs().SaveProfileAsync(
            default, default!, default!, default!, default);
    }
}
