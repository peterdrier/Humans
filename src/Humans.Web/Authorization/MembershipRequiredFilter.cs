using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Humans.Domain.Enums;

namespace Humans.Web.Authorization;

/// <summary>
/// Global filter routing authenticated users by their stored <see cref="UserState"/>:
/// only <see cref="UserState.Active"/> reaches the app. <see cref="UserState.Bare"/> → name entry;
/// <see cref="UserState.DeletePending"/> → the cancel-deletion screen; Suspended/Rejected/Deleted/
/// Merged → the account-status wall. Exempt controllers are public/self-gated pages, the onboarding
/// surface, and the redirect targets themselves (so non-Active users can reach their landing).
/// </summary>
public class MembershipRequiredFilter : IAsyncActionFilter
{
    private static readonly HashSet<string> ExemptControllers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Account",          // Login/logout/OAuth
        "GovernanceApplications", // Tier application submission
        "Consent",          // Sign required legal documents (onboarding surface)
        "Profile",          // Profile setup (onboarding surface)
        "Admin",            // Has its own Roles = "Admin" gate
        "Board",            // Has its own Roles = "Board,Admin" gate
        "Language",         // Language switching
        "OnboardingReview", // Has its own coordinator/Board role gate
        "Camp",             // Public camps pages ([AllowAnonymous])
        "CampAdmin",        // Has its own Roles = "CampAdmin,Admin" gate
        "CampApi",          // Public API ([AllowAnonymous])
        "Feedback",         // Feedback submission — accessible to all authenticated users
        "FeedbackApi",      // API key auth, no membership required
        "Guest",            // Profileless account dashboard (being folded into Bare)
        "Legal",            // Public legal documents ([AllowAnonymous])
        "Notifications",    // Notification inbox — accessible to all authenticated users
        "OnboardingWidget", // Guided onboarding (name entry) — the Bare landing target
        "User",             // Account-status wall + cancel-deletion landing (redirect targets)
    };

    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.User;

        if (user.Identity?.IsAuthenticated != true)
        {
            return next();
        }

        if (RoleChecks.BypassesMembershipRequirement(user))
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
            UserState.Suspended or UserState.Rejected or UserState.Deleted or UserState.Merged
                => new RedirectToActionResult("Status", "User", null),
            // Bare or null (not yet named / unseeded) → name entry.
            _ => new RedirectToActionResult("Index", "OnboardingWidget", null),
        };
        return Task.CompletedTask;
    }
}
