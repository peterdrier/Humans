using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Humans.Domain.Constants;

namespace Humans.Web.Authorization;

/// <summary>
/// Global action filter that restricts most of the app to active Volunteers team members.
/// Users who haven't been approved or haven't consented to required docs are redirected
/// to the home dashboard, which shows them what steps remain.
/// </summary>
public class MembershipRequiredFilter : IAsyncActionFilter
{
    // Controllers accessible without active membership (onboarding flow + public pages)
    private static readonly HashSet<string> ExemptControllers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Home",        // Public landing + dashboard (shows onboarding status)
        "Account",     // Login/logout/OAuth
        "Application", // Submit membership application
        "Consent",     // Sign required legal documents
        "Profile",     // Set up profile during onboarding
        "Admin",       // Has its own Roles = "Board,Admin" gate
        "Human",       // Public profile viewing
        "Language",         // Language switching
        "OnboardingReview", // Has its own coordinator/Board role gate
    };

    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.User;

        // Not authenticated — let normal auth handle it
        if (user.Identity?.IsAuthenticated != true)
        {
            return next();
        }

        // Admin/Board/Coordinator bypass — they always have access
        if (user.IsInRole(RoleNames.Admin) || user.IsInRole(RoleNames.Board) ||
            user.IsInRole(RoleNames.ConsentCoordinator) || user.IsInRole(RoleNames.VolunteerCoordinator))
        {
            return next();
        }

        // Exempt controllers — accessible during onboarding
        if (context.Controller is Controller controller)
        {
            var controllerName = controller.ControllerContext.ActionDescriptor.ControllerName;
            if (ExemptControllers.Contains(controllerName))
            {
                return next();
            }
        }

        // Check ActiveMember claim (set by RoleAssignmentClaimsTransformation)
        var isActiveMember = user.HasClaim(c =>
            string.Equals(c.Type, RoleAssignmentClaimsTransformation.ActiveMemberClaimType, StringComparison.Ordinal) &&
            string.Equals(c.Value, "true", StringComparison.Ordinal));

        if (isActiveMember)
        {
            return next();
        }

        // Not an active member — redirect to dashboard which shows onboarding steps
        context.Result = new RedirectToActionResult("Index", "Home", null);
        return Task.CompletedTask;
    }
}
