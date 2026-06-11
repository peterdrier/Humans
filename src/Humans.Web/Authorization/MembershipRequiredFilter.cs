using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Humans.Domain.Enums;

namespace Humans.Web.Authorization;

/// <summary>
/// Global filter routing authenticated users by their stored <see cref="UserState"/>:
/// only <see cref="UserState.Active"/> reaches the app. <see cref="UserState.Bare"/> → name entry;
/// <see cref="UserState.DeletePending"/> → the cancel-deletion screen; Suspended/AdminSuspended/
/// Rejected/Deleted/Merged → the account-status wall. Exempt controllers are public/self-gated pages, the onboarding
/// surface, and the redirect targets themselves (so non-Active users can reach their landing).
/// </summary>
public class MembershipRequiredFilter : IAsyncActionFilter
{
    // Only controllers a non-Active user must still reach. Public controllers use
    // [AllowAnonymous]/API keys; role-gated app controllers are still blocked until Active.
    private static readonly HashSet<string> ExemptControllers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Account",          // Login/logout/OAuth
        "OnboardingWidget", // Guided onboarding (name entry) — the Bare landing target
        "Profile",          // Profile setup (onboarding surface)
        "Consent",          // Sign required legal documents (onboarding surface)
        "User",             // Account-status wall + cancel-deletion landing (redirect targets)
        "Language",         // Language switching
        "Guest",            // Profileless account dashboard
        "GovernanceApplications", // Tier application submission — any logged-in user
        "Feedback",         // Feedback submission — any logged-in user
        "Notifications",    // Notification inbox — any logged-in user
        "Survey",           // Tokenised survey answering — invited non-Active users must still reach it ([AllowAnonymous])
    };

    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.User;

        if (user.Identity?.IsAuthenticated != true)
        {
            return next();
        }

        if (context.ActionDescriptor is ControllerActionDescriptor cad &&
            (cad.MethodInfo.IsDefined(typeof(AllowAnonymousAttribute), true) ||
             cad.ControllerTypeInfo.IsDefined(typeof(AllowAnonymousAttribute), true)))
        {
            return next();
        }

        if (context.Controller is Controller controller &&
            ExemptControllers.Contains(controller.ControllerContext.ActionDescriptor.ControllerName))
        {
            return next();
        }

        // Access is the stored UserState (stamped on the principal by
        // RoleAssignmentClaimsTransformation). Only Active reaches the app.
        var state = RoleAssignmentClaimsTransformation.GetUserState(user);
        if (state == UserState.Active)
        {
            return next();
        }

        context.Result = state switch
        {
            UserState.DeletePending => new RedirectToActionResult("Deletion", "User", null),
            UserState.Suspended or UserState.AdminSuspended
                or UserState.Rejected or UserState.Deleted or UserState.Merged
                => new RedirectToActionResult("Status", "User", null),
            // Bare or null (not yet named / unseeded) → name entry.
            _ => new RedirectToActionResult("Index", "OnboardingWidget", null),
        };
        return Task.CompletedTask;
    }
}
