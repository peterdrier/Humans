using System.Security.Claims;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Testing;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
    private readonly IOnboardingWidgetState _state = Substitute.For<IOnboardingWidgetState>();
    private readonly IProfileService _profile = Substitute.For<IProfileService>();

    private OnboardingWidgetController BuildSut(Guid userId)
    {
        var ctrl = new OnboardingWidgetController(_state, _profile);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) },
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
