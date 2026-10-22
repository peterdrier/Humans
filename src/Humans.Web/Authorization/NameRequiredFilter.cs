using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Humans.Users.Contracts;

namespace Humans.Web.Authorization;

/// <summary>
/// Onboarding name-gate: any authenticated user whose Profile has no real <c>BurnerName</c> is redirected to the
/// burner + legal-name form first (<c>memory/architecture/burnername-is-the-display-name.md</c>).
/// </summary>
/// <remarks>
/// Only ever <em>redirects</em> an already-authenticated user; it never blocks sign-in. The check reads the
/// cache-backed <see cref="UserInfo"/>, refreshed on every profile save, so the gate opens on the very next request.
/// </remarks>
public sealed class NameRequiredFilter(IUserService userService) : IAsyncActionFilter
{
    // Allow-list (whole controllers) — reaching these must never be gated, or the
    // gate redirects to itself / locks the user out of escaping it.
    private static readonly HashSet<string> ExemptControllers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Account",  // Login / logout / OAuth + magic-link callbacks.
        "Language", // Language switching while on the gate.
    };

    // OnboardingWidget is deliberately not exempt wholesale, only its Names form: its later steps don't all re-check
    // names and ShiftSignupService.SignUpAsync never validates them, so a nameless user could POST a rota signup.
    private static readonly HashSet<(string Controller, string Action)> ExemptActions =
        new()
        {
            ("OnboardingWidget", "Names"), // The gate form itself (GET + POST).
            ("Home", "Error"),
            ("Home", "Privacy"),
        };

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.User;

        if (user.Identity?.IsAuthenticated != true)
        {
            await next();
            return;
        }

        // Machine requests carry a Backdoor API key, not a browsing session: a JSON client cannot fill in the name form.
        if (MembershipRequiredFilter.IsMachineRequest(user))
        {
            await next();
            return;
        }

        if (context.ActionDescriptor is ControllerActionDescriptor cad &&
            (cad.MethodInfo.IsDefined(typeof(AllowAnonymousAttribute), true) ||
             cad.ControllerTypeInfo.IsDefined(typeof(AllowAnonymousAttribute), true)))
        {
            await next();
            return;
        }

        if (context.Controller is Controller controller)
        {
            var descriptor = controller.ControllerContext.ActionDescriptor;
            if (ExemptControllers.Contains(descriptor.ControllerName) ||
                ExemptActions.Contains((descriptor.ControllerName, descriptor.ActionName)))
            {
                await next();
                return;
            }
        }

        // Anonymization deliberately clears names. Let the membership gate route
        // terminal accounts to their status wall instead of offering name entry again.
        if (RoleAssignmentClaimsTransformation.GetUserState(user) is UserState.Deleted or UserState.Merged)
        {
            await next();
            return;
        }

        var raw = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(raw, out var userId))
        {
            await next();
            return;
        }

        // Cache-backed read — warm for the request (the claims transformation reads
        // the same UserInfo) and refreshed on profile save, so the gate opens the
        // moment names are submitted.
        var info = await userService.GetUserInfoAsync(userId, context.HttpContext.RequestAborted);
        if (info is null || info.HasRequiredNameFields)
        {
            await next();
            return;
        }

        context.Result = new RedirectToActionResult("Names", "OnboardingWidget", null);
    }
}
