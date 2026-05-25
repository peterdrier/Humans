using System.Security.Claims;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Controllers;

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
    private readonly IShiftSignupService _signups = Substitute.For<IShiftSignupService>();
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    private readonly IConsentService _consents = Substitute.For<IConsentService>();
    private readonly IOnboardingService _onboardingService = Substitute.For<IOnboardingService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IStringLocalizer<SharedResource> _localizer =
        Substitute.For<IStringLocalizer<SharedResource>>();

    public OnboardingWidgetControllerDispatcherTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);
        _localizer[Arg.Any<string>()].Returns(ci =>
            new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
    }

    private OnboardingWidgetController BuildSut(Guid userId)
    {
        var user = new User { Id = userId };
        _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(user);
        var ctrl = new OnboardingWidgetController(_userService, _state, _profileEditor, _signups, _shiftMgmt, _consents, _onboardingService, _localizer);
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

        var result = await ctrl.Index(CancellationToken.None);

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

        var result = await ctrl.Index(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Home", redirect.ControllerName);
    }
}
