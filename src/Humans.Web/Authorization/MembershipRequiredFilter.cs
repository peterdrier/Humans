using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Humans.Web.Authorization;

/// <summary>
/// Global filter restricting most of the app to active Volunteers team members; non-members redirect to Home/Guest dashboard.
/// </summary>
public class MembershipRequiredFilter : IAsyncActionFilter
{
    // Onboarding flow + public pages exempt from active-membership gate.
    private static readonly HashSet<string> ExemptControllers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Home",        // Public landing + dashboard (shows onboarding status)
        "Account",     // Login/logout/OAuth
        "GovernanceApplications", // Submit membership application
        "Consent",     // Sign required legal documents
        "Profile",     // Set up profile during onboarding
        "Admin",       // Has its own Roles = "Admin" gate
        "Board",       // Has its own Roles = "Board,Admin" gate
        "Language",         // Language switching
        "OnboardingReview", // Has its own coordinator/Board role gate
        "Camp",             // Public camps pages ([AllowAnonymous])
        "CampAdmin",        // Has its own Roles = "CampAdmin,Admin" gate
        "CampApi",          // Public API ([AllowAnonymous])
        "Feedback",         // Feedback submission — accessible to all authenticated users
        "FeedbackApi",      // API key auth, no membership required
        "Guest",            // Profileless account dashboard
        "Legal",            // Public legal documents ([AllowAnonymous])
        "Notifications",    // Notification inbox — accessible to all authenticated users
        "OnboardingWidget", // Guided onboarding flow (Names → Shifts → Consents) — used by mid-onboarding users
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

        if (context.Controller is Controller controller)
        {
            var controllerName = controller.ControllerContext.ActionDescriptor.ControllerName;
            if (ExemptControllers.Contains(controllerName))
            {
                return next();
            }
        }

        // ActiveMember claim is set by RoleAssignmentClaimsTransformation.
        var isActiveMember = user.HasClaim(c =>
            string.Equals(c.Type, RoleAssignmentClaimsTransformation.ActiveMemberClaimType, StringComparison.Ordinal) &&
            string.Equals(c.Value, RoleAssignmentClaimsTransformation.ActiveClaimValue, StringComparison.Ordinal));

        if (isActiveMember)
        {
            return next();
        }

        // Profileless → Guest; onboarding (has profile) → Home.
        var hasProfile = user.HasClaim(c =>
            string.Equals(c.Type, RoleAssignmentClaimsTransformation.HasProfileClaimType, StringComparison.Ordinal) &&
            string.Equals(c.Value, RoleAssignmentClaimsTransformation.ActiveClaimValue, StringComparison.Ordinal));

        context.Result = hasProfile
            ? new RedirectToActionResult("Index", "Home", null)
            : new RedirectToActionResult("Index", "Guest", null);
        return Task.CompletedTask;
    }
}
