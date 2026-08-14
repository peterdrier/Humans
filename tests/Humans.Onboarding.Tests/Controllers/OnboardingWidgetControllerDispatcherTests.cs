using System.Security.Claims;
using Humans.Consent;
using Humans.Consent.Contracts;
using Humans.Application;
using Humans.Onboarding.Contracts;
using Humans.Onboarding.Services;
using Humans.Users.Contracts;
using Humans.Shifts.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.UI;
using Humans.Onboarding.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Xunit;

namespace Humans.Onboarding.Tests.Controllers;

/// <summary>
/// Verifies that <see cref="OnboardingWidgetController.Index"/> — the canonical
/// dispatcher entry point linked from /Welcome, Home/Guest redirects, and the
/// layout banner — routes the user to the correct step action based on
/// <see cref="IOnboardingWidgetState.GetCurrentStepAsync"/>.
/// </summary>
public class OnboardingWidgetControllerDispatcherTests
{
    private readonly UserManager<User> _userManager;
    private readonly IOnboardingWidgetState _state = Substitute.For<IOnboardingWidgetState>();
    private readonly IProfileEditorService _profileEditor = Substitute.For<IProfileEditorService>();
    private readonly IShiftSignups _signups = Substitute.For<IShiftSignups>();
    private readonly IShiftManagementServiceRead _shiftMgmt = Substitute.For<IShiftManagementServiceRead>();
    private readonly IBurnSettingsService _burnSettings = Substitute.For<IBurnSettingsService>();
    private readonly IShiftView _shiftView = Substitute.For<IShiftView>();
    private readonly IConsentSubmission _consents = Substitute.For<IConsentSubmission>();
    private readonly IOnboardingService _onboardingService = Substitute.For<IOnboardingService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IStringLocalizer<ConsentResource> _consentLocalizer =
        Substitute.For<IStringLocalizer<ConsentResource>>();

    private readonly IStringLocalizer<OnboardingResource> _localizer =
        Substitute.For<IStringLocalizer<OnboardingResource>>();

    public OnboardingWidgetControllerDispatcherTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);
        _localizer[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        _consentLocalizer[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
    }

    private OnboardingWidgetController BuildSut(Guid userId)
    {
        var user = new User { Id = userId };
        _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(user);
        var ctrl = new OnboardingWidgetController(_userService, _state, _profileEditor, _signups, _shiftMgmt, _burnSettings, _shiftView, _consents, _onboardingService, NodaTime.SystemClock.Instance, _localizer, _consentLocalizer);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                "test")),
        };
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctrl;
    }

    [HumansTheory]
    [InlineData(OnboardingWidgetStep.Names, "Names")]
    [InlineData(OnboardingWidgetStep.Shifts, "Shifts")]
    [InlineData(OnboardingWidgetStep.Consents, "Consents")]
    public async Task Index_RedirectsToCurrentStep(OnboardingWidgetStep step, string action)
    {
        var userId = Guid.NewGuid();
        _state.GetCurrentStepAsync(userId, Arg.Any<CancellationToken>()).Returns(step);
        var ctrl = BuildSut(userId);

        var result = await ctrl.Index(TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(action, redirect.ActionName);
        Assert.Null(redirect.ControllerName);
    }

    [HumansFact]
    public async Task Index_RedirectsToHome_WhenComplete()
    {
        var userId = Guid.NewGuid();
        _state.GetCurrentStepAsync(userId, Arg.Any<CancellationToken>())
            .Returns(OnboardingWidgetStep.Complete);
        var ctrl = BuildSut(userId);

        var result = await ctrl.Index(TestContext.Current.CancellationToken);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Home", redirect.ControllerName);
    }
}
