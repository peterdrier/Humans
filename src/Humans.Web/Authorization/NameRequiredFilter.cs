using System.Security.Claims;
using Humans.Application.Interfaces.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Humans.Web.Authorization;

/// <summary>
/// Onboarding name-gate. Any authenticated user whose Profile has no real
/// <c>BurnerName</c> (Stub profile, or an Active profile with blank required
/// names) is redirected to the burner + legal-name form before they can reach
/// the rest of the app.
/// <para>
/// This is the single gate that covers OAuth/Google first sign-in, imported
/// contacts hitting the magic-link <c>ExistingUser</c> branch, and legacy
/// accounts still sitting with a blank BurnerName — see
/// nobodies-collective/Humans#812 and
/// <c>memory/architecture/burnername-is-the-display-name.md</c>.
/// </para>
/// <para>
/// Runs strictly after authentication and only ever <em>redirects</em> an
/// already-authenticated user — it never blocks sign-in (see the never-block-sign-in
/// hard rule). The required-name check reads the cache-backed <see cref="UserInfo"/>
/// (refreshed on every profile save), so a user who submits the form sees the gate
/// open on the very next request — no stale-cache redirect loop.
/// </para>
/// </summary>
public sealed class NameRequiredFilter(IUserServiceRead userService) : IAsyncActionFilter
{
    // Allow-list (whole controllers) — reaching these must never be gated, or the
    // gate redirects to itself / locks the user out of escaping it.
    private static readonly HashSet<string> ExemptControllers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Account",  // Login / logout / OAuth + magic-link callbacks.
        "Language", // Language switching while on the gate.
    };

    // Allow-list (specific controller actions) — pages the gate form links to or
    // that must render even mid-gate (error pages, privacy notice).
    //
    // OnboardingWidget is deliberately NOT exempt wholesale — only its Names form
    // is. The widget's later steps (Shifts/SignUp/SignUpRange/Skip and the
    // Consents pair) do not all re-check names, and ShiftSignupService.SignUpAsync
    // never validates HasRequiredNameFields, so a whole-controller exemption would
    // let a nameless user POST a rota signup via OnboardingWidget/SignUp without
    // ever naming themselves. Gating every route except Names sends a nameless
    // user to the form first (the dispatcher, Index, gates the same way and so
    // needs no exemption); once named, the gate passes and the wizard flow —
    // Names → Shifts → Consents — is untouched.
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
