using System.Reflection;
using System.Security.Claims;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
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
/// Verifies <see cref="MembershipRequiredFilter"/> routes authenticated users by their stored
/// <see cref="UserState"/>: only <see cref="UserState.Active"/> reaches a non-exempt controller;
/// <see cref="UserState.Bare"/> (and unseeded null) go to the onboarding widget;
/// <see cref="UserState.DeletePending"/> goes to the cancel-deletion screen; and the walled states
/// go to the account-status page. Exempt controllers and anonymous requests pass straight through.
/// </summary>
public class MembershipRequiredFilterTests
{
    [HumansFact]
    public async Task Active_user_reaches_a_non_exempt_controller()
    {
        var (result, nextCalled) = await RunAsync("Home", "Index", state: UserState.Active);

        Assert.True(nextCalled, "Active users must reach the app");
        Assert.Null(result);
    }

    [HumansFact]
    public async Task Bare_user_routed_to_onboarding_widget()
    {
        var (result, nextCalled) = await RunAsync("Home", "Index", state: UserState.Bare);

        Assert.False(nextCalled);
        AssertRedirect(result, "Index", "OnboardingWidget");
    }

    [HumansFact]
    public async Task Unseeded_null_state_routed_to_onboarding_widget()
    {
        var (result, nextCalled) = await RunAsync("Home", "Index", state: null);

        Assert.False(nextCalled);
        AssertRedirect(result, "Index", "OnboardingWidget");
    }

    [HumansFact]
    public async Task DeletePending_user_routed_to_cancel_deletion_screen()
    {
        var (result, nextCalled) = await RunAsync("Home", "Index", state: UserState.DeletePending);

        Assert.False(nextCalled);
        AssertRedirect(result, "Deletion", "User");
    }

    [HumansTheory]
    [InlineData(UserState.Suspended)]
    [InlineData(UserState.AdminSuspended)]
    [InlineData(UserState.Rejected)]
    [InlineData(UserState.Deleted)]
    [InlineData(UserState.Merged)]
    public async Task Walled_states_routed_to_account_status_page(UserState state)
    {
        var (result, nextCalled) = await RunAsync("Home", "Index", state: state);

        Assert.False(nextCalled);
        AssertRedirect(result, "Status", "User");
    }

    [HumansFact]
    public async Task Exempt_onboarding_controller_passes_through_for_non_active_user()
    {
        var (result, nextCalled) = await RunAsync("OnboardingWidget", "Names", state: UserState.Bare);

        Assert.True(nextCalled, "the onboarding surface is the Bare landing target and must not redirect");
        Assert.Null(result);
    }

    [HumansFact]
    public async Task Role_holder_does_not_bypass_state_routing()
    {
        var (result, nextCalled) = await RunAsync("Home", "Index", state: UserState.Bare, role: RoleNames.Admin);

        Assert.False(nextCalled);
        AssertRedirect(result, "Index", "OnboardingWidget");
    }

    [HumansFact]
    public async Task Anonymous_request_passes_through()
    {
        var (result, nextCalled) = await RunAsync("Home", "Index", state: null, authenticated: false);

        Assert.True(nextCalled);
        Assert.Null(result);
    }

    private static async Task<(IActionResult? Result, bool NextCalled)> RunAsync(
        string controllerName,
        string actionName,
        UserState? state,
        bool authenticated = true,
        string? role = null)
    {
        var sut = new MembershipRequiredFilter();
        var ctx = BuildExecutingContext(controllerName, actionName, authenticated, state, role);
        var nextCalled = false;

        await sut.OnActionExecutionAsync(ctx, () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        return (ctx.Result, nextCalled);
    }

    private static void AssertRedirect(IActionResult? result, string action, string controller)
    {
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(action, redirect.ActionName);
        Assert.Equal(controller, redirect.ControllerName);
    }

    private static ActionExecutingContext BuildExecutingContext(
        string controllerName,
        string actionName,
        bool authenticated,
        UserState? state,
        string? role)
    {
        ClaimsIdentity identity;
        if (authenticated)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
            if (state is { } s)
            {
                claims.Add(new Claim(RoleAssignmentClaimsTransformation.UserStateClaimType, s.ToString()));
            }
            if (role is not null)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }
            identity = new ClaimsIdentity(claims, authenticationType: "test");
        }
        else
        {
            identity = new ClaimsIdentity();
        }

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
