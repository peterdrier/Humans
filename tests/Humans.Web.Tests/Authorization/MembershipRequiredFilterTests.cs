using System.Reflection;
using System.Security.Claims;
using Humans.Web.Authorization;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Humans.Web.Tests.Authorization;

/// <summary>
/// Verifies that <see cref="MembershipRequiredFilter"/> exempts onboarding-flow
/// controllers so users mid-onboarding (no profile or no consents yet) can still
/// reach the widget pages without being bounced through a redirect loop.
/// </summary>
public class MembershipRequiredFilterTests
{
    [HumansFact]
    public async Task OnboardingWidget_IsExempt_ForProfilelessAuthenticatedUser()
    {
        var sut = new MembershipRequiredFilter();
        var ctx = BuildExecutingContext("OnboardingWidget", "Names", authenticated: true, hasProfile: false);
        var nextCalled = false;

        await sut.OnActionExecutionAsync(ctx, () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.True(nextCalled, "Filter should let OnboardingWidget through, not short-circuit");
        Assert.Null(ctx.Result);
    }

    [HumansFact]
    public async Task OnboardingWidget_IsExempt_ForProfiledNonActiveMember()
    {
        var sut = new MembershipRequiredFilter();
        var ctx = BuildExecutingContext("OnboardingWidget", "Consents", authenticated: true, hasProfile: true);
        var nextCalled = false;

        await sut.OnActionExecutionAsync(ctx, () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.True(nextCalled);
        Assert.Null(ctx.Result);
    }

    private static ActionExecutingContext BuildExecutingContext(
        string controllerName,
        string actionName,
        bool authenticated,
        bool hasProfile)
    {
        var identity = authenticated
            ? new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                }.Concat(hasProfile
                    ? new[]
                    {
                        new Claim(
                            RoleAssignmentClaimsTransformation.HasProfileClaimType,
                            RoleAssignmentClaimsTransformation.ActiveClaimValue),
                    }
                    : Array.Empty<Claim>()),
                authenticationType: "test")
            : new ClaimsIdentity();

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
        };

        // Use a real controller type so the action descriptor resolves a valid
        // ControllerTypeInfo — required for the AllowAnonymous reflection check.
        var controllerType = controllerName switch
        {
            "OnboardingWidget" => typeof(OnboardingWidgetController),
            _ => typeof(HomeController),
        };
        var actionDescriptor = new ControllerActionDescriptor
        {
            ControllerName = controllerName,
            ActionName = actionName,
            ControllerTypeInfo = controllerType.GetTypeInfo(),
            MethodInfo = controllerType.GetMethods()
                .FirstOrDefault(m => string.Equals(m.Name, actionName, StringComparison.Ordinal))
                ?? typeof(MembershipRequiredFilterTests).GetMethod(nameof(BuildExecutingContext),
                    BindingFlags.NonPublic | BindingFlags.Static)!,
        };

        var actionContext = new ActionContext(
            http,
            new RouteData(),
            actionDescriptor);

        // Provide a fake controller instance — the filter only reads its
        // ControllerContext.ActionDescriptor.ControllerName.
        var stubController = new StubController
        {
            ControllerContext = new ControllerContext(actionContext),
        };

        return new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(StringComparer.Ordinal),
            controller: stubController);
    }

    private sealed class StubController : Controller;
}
