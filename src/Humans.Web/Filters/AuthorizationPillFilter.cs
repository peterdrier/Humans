using System.Reflection;
using Humans.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Humans.Web.Filters;

/// <summary>
/// Reads [Authorize(Roles = "...")] from the current action and controller,
/// formats the role list into friendly group names, and sets ViewData["AuthPillRoles"]
/// so the layout can render an authorization indicator pill.
/// Only runs for authenticated users who already have access to the page.
/// </summary>
public class AuthorizationPillFilter : IActionFilter
{
    // Map raw role names to user-friendly display labels
    private static readonly Dictionary<string, string> RoleDisplayNames = new(StringComparer.Ordinal)
    {
        [RoleNames.Admin] = "Admin",
        [RoleNames.Board] = "Board",
        [RoleNames.ConsentCoordinator] = "Consent Coordinator",
        [RoleNames.VolunteerCoordinator] = "Volunteer Coordinator",
        [RoleNames.TeamsAdmin] = "Teams Admin",
        [RoleNames.CampAdmin] = "Camp Admin",
        [RoleNames.TicketAdmin] = "Ticket Admin",
        [RoleNames.NoInfoAdmin] = "NoInfo Admin",
        [RoleNames.FeedbackAdmin] = "Feedback Admin",
        [RoleNames.HumanAdmin] = "Human Admin",
        [RoleNames.FinanceAdmin] = "Finance Admin"
    };

    public void OnActionExecuting(ActionExecutingContext context)
    {
        // Only show pill to authenticated users
        if (context.HttpContext.User.Identity?.IsAuthenticated != true)
            return;

        if (context.ActionDescriptor is not ControllerActionDescriptor descriptor)
            return;

        // Skip if action has [AllowAnonymous] — endpoint is open despite controller-level [Authorize]
        if (descriptor.MethodInfo.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any())
            return;

        // Collect all [Authorize(Roles = "...")] from action then controller
        var roles = new HashSet<string>(StringComparer.Ordinal);

        // Action-level attributes take precedence for display
        var actionAuthAttrs = descriptor.MethodInfo
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true);
        foreach (var attr in actionAuthAttrs)
        {
            if (!string.IsNullOrEmpty(attr.Roles))
            {
                foreach (var role in attr.Roles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    roles.Add(role);
                }
            }
        }

        // Controller-level attributes
        var controllerAuthAttrs = descriptor.ControllerTypeInfo
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true);
        foreach (var attr in controllerAuthAttrs)
        {
            if (!string.IsNullOrEmpty(attr.Roles))
            {
                foreach (var role in attr.Roles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    roles.Add(role);
                }
            }
        }

        // If no role-based restrictions, no pill to show
        if (roles.Count == 0)
            return;

        // Admin has full access — only show "Admin only" when Admin is the sole role
        var hasAdmin = roles.Remove(RoleNames.Admin);
        if (roles.Count == 0)
        {
            if (hasAdmin && context.Controller is Controller adminController)
            {
                adminController.ViewData["AuthPillRoles"] = "Admin only";
            }
            return;
        }

        // Convert non-Admin roles to display names
        var displayNames = roles
            .Select(r => RoleDisplayNames.TryGetValue(r, out var display) ? display : r)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

        if (context.Controller is Controller controller)
        {
            controller.ViewData["AuthPillRoles"] = string.Join(" \u00b7 ", displayNames);
        }
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
        // No post-action processing needed
    }
}
