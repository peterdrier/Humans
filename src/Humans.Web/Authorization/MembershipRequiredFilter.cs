using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Humans.Backdoor.Contracts;
using Humans.Base.Attributes;
using Humans.Users.Contracts;

namespace Humans.Web.Authorization;

/// <summary>
/// Global filter routing authenticated users by their stored <see cref="UserState"/>:
/// only <see cref="UserState.Active"/> reaches the app. <see cref="UserState.Bare"/> → name entry;
/// <see cref="UserState.DeletePending"/> → the cancel-deletion screen; Suspended/AdminSuspended/
/// Rejected/Deleted/Merged → the account-status wall. A controller or action a non-Active account must still
/// reach (the onboarding surface, the redirect targets themselves, own-profile maintenance) carries
/// <see cref="AllowBeforeActiveMembershipAttribute"/> in its own section; public pages use <c>[AllowAnonymous]</c>.
/// </summary>
public class MembershipRequiredFilter : IAsyncActionFilter
{
    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.User;

        if (user.Identity?.IsAuthenticated != true)
        {
            return next();
        }

        // A Backdoor API key authenticates a machine, not a browsing session: there is no onboarding page to send a
        // JSON client to, and the key's owner never passes through claims transformation, so the state claim this
        // gate reads is absent by construction.
        if (IsMachineRequest(user))
        {
            return next();
        }

        var action = context.ActionDescriptor as ControllerActionDescriptor;
        if (action is not null && IsPublicEndpoint(action))
        {
            return next();
        }

        var allowance = action is null ? null : Allowance(action);

        // A lingering cookie must not turn an anonymized account into an editable profile.
        // Terminal accounts retain the status wall and session/language routes, but none
        // of the onboarding or self-service allowances for recoverable accounts.
        var state = RoleAssignmentClaimsTransformation.GetUserState(user);
        if (state is UserState.Deleted or UserState.Merged)
        {
            if (allowance is { Terminal: true })
            {
                return next();
            }

            context.Result = new RedirectToActionResult("Status", "User", null);
            return Task.CompletedTask;
        }

        if (allowance is not null)
        {
            return next();
        }

        // Access is the stored UserState (stamped on the principal by
        // RoleAssignmentClaimsTransformation). Only Active reaches the app.
        if (state == UserState.Active)
        {
            return next();
        }

        context.Result = state switch
        {
            UserState.DeletePending => new RedirectToActionResult("Deletion", "User", null),
            UserState walled when walled.IsWalled() => new RedirectToActionResult("Status", "User", null),
            // Bare or null (not yet named / unseeded) → name entry.
            _ => new RedirectToActionResult("Index", "OnboardingWidget", null),
        };
        return Task.CompletedTask;
    }

    /// <summary>The action's own allowance first, then its controller's.</summary>
    internal static AllowBeforeActiveMembershipAttribute? Allowance(ControllerActionDescriptor cad) =>
        cad.MethodInfo.GetCustomAttribute<AllowBeforeActiveMembershipAttribute>(inherit: true)
        ?? cad.ControllerTypeInfo.GetCustomAttribute<AllowBeforeActiveMembershipAttribute>(inherit: true);

    /// <summary>
    /// An <c>[AllowAnonymous]</c> endpoint is reachable in any account state, with one exception: <c>Home/Index</c>
    /// is the public landing page and the member dashboard on one route, so a signed-in user is still routed by
    /// state (here) and by name (<see cref="NameRequiredFilter"/>).
    /// </summary>
    internal static bool IsPublicEndpoint(ControllerActionDescriptor cad) =>
        (cad.MethodInfo.IsDefined(typeof(AllowAnonymousAttribute), true)
         || cad.ControllerTypeInfo.IsDefined(typeof(AllowAnonymousAttribute), true))
        && cad is not { ControllerName: "Home", ActionName: "Index" };

    /// <summary>True when the principal came from a Backdoor API key rather than a sign-in cookie.</summary>
    internal static bool IsMachineRequest(ClaimsPrincipal user) =>
        string.Equals(user.Identity?.AuthenticationType, BackdoorAuthentication.SchemeName, StringComparison.Ordinal);
}
