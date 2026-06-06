using System.Security.Claims;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Verifies that <see cref="WelcomeController.Index"/> renders the explainer
/// for anonymous visitors, redirects active members to /Shifts, and routes
/// authenticated-but-not-active users into the onboarding widget instead of
/// re-rendering the welcome page.
/// </summary>
public class WelcomeControllerTests
{
    private static WelcomeController BuildSut(ClaimsPrincipal user)
    {
        var ctrl = new WelcomeController();
        var http = new DefaultHttpContext { User = user };
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctrl;
    }

    [HumansFact]
    public void Welcome_AnonymousVisitor_ReturnsView()
    {
        var ctrl = BuildSut(new ClaimsPrincipal(new ClaimsIdentity()));

        var result = ctrl.Index();

        Assert.IsType<ViewResult>(result);
    }

    [HumansFact]
    public void Welcome_ActiveState_RedirectsToShifts()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(
                RoleAssignmentClaimsTransformation.UserStateClaimType,
                UserState.Active.ToString()),
        };
        var ctrl = BuildSut(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));

        var result = ctrl.Index();

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/Shifts", redirect.Url);
    }

    [HumansFact]
    public void Welcome_AuthenticatedNonActive_RedirectsToOnboardingWidget()
    {
        // Authenticated but no Active UserState claim (e.g. Bare) — should go to the widget.
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(
                RoleAssignmentClaimsTransformation.UserStateClaimType,
                UserState.Bare.ToString()),
        };
        var ctrl = BuildSut(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));

        var result = ctrl.Index();

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/OnboardingWidget", redirect.Url);
    }
}
