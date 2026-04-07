using System.Reflection;
using Humans.Domain.Constants;
using Humans.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Humans.Web.Filters;

/// <summary>
/// Reads [Authorize(Roles = "...")] and [Authorize(Policy = "...")] from the current
/// action and controller, formats the role list into friendly group names, and sets
/// ViewData["AuthPillRoles"] so the layout can render an authorization indicator pill.
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

    // Map policy names to their constituent roles for pill display
    private static readonly Dictionary<string, string[]> PolicyRoles = new(StringComparer.Ordinal)
    {
        [PolicyNames.AdminOnly] = [RoleNames.Admin],
        [PolicyNames.BoardOnly] = [RoleNames.Board],
        [PolicyNames.BoardOrAdmin] = [RoleNames.Board, RoleNames.Admin],
        [PolicyNames.HumanAdminBoardOrAdmin] = [RoleNames.HumanAdmin, RoleNames.Board, RoleNames.Admin],
        [PolicyNames.HumanAdminOrAdmin] = [RoleNames.HumanAdmin, RoleNames.Admin],
        [PolicyNames.TeamsAdminBoardOrAdmin] = [RoleNames.TeamsAdmin, RoleNames.Board, RoleNames.Admin],
        [PolicyNames.CampAdminOrAdmin] = [RoleNames.CampAdmin, RoleNames.Admin],
        [PolicyNames.TicketAdminBoardOrAdmin] = [RoleNames.TicketAdmin, RoleNames.Admin, RoleNames.Board],
        [PolicyNames.TicketAdminOrAdmin] = [RoleNames.TicketAdmin, RoleNames.Admin],
        [PolicyNames.FeedbackAdminOrAdmin] = [RoleNames.FeedbackAdmin, RoleNames.Admin],
        [PolicyNames.FinanceAdminOrAdmin] = [RoleNames.FinanceAdmin, RoleNames.Admin],
        [PolicyNames.ReviewQueueAccess] = [RoleNames.ConsentCoordinator, RoleNames.VolunteerCoordinator, RoleNames.Board, RoleNames.Admin],
        [PolicyNames.ConsentCoordinatorBoardOrAdmin] = [RoleNames.ConsentCoordinator, RoleNames.Board, RoleNames.Admin],
        [PolicyNames.ShiftDashboardAccess] = [RoleNames.Admin, RoleNames.NoInfoAdmin, RoleNames.VolunteerCoordinator],
        [PolicyNames.ShiftDepartmentManager] = [RoleNames.Admin, RoleNames.NoInfoAdmin, RoleNames.VolunteerCoordinator],
        [PolicyNames.PrivilegedSignupApprover] = [RoleNames.Admin, RoleNames.NoInfoAdmin],
        [PolicyNames.VolunteerManager] = [RoleNames.Admin, RoleNames.VolunteerCoordinator],
        [PolicyNames.VolunteerSectionAccess] = [RoleNames.TeamsAdmin, RoleNames.Board, RoleNames.Admin, RoleNames.VolunteerCoordinator],
        [PolicyNames.MedicalDataViewer] = [RoleNames.Admin, RoleNames.NoInfoAdmin],
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

        // Collect roles from [Authorize(Roles = "...")] and [Authorize(Policy = "...")] from action then controller
        var roles = new HashSet<string>(StringComparer.Ordinal);

        // Action-level attributes take precedence for display
        var actionAuthAttrs = descriptor.MethodInfo
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true);
        CollectRolesFromAttributes(actionAuthAttrs, roles);

        // Controller-level attributes
        var controllerAuthAttrs = descriptor.ControllerTypeInfo
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true);
        CollectRolesFromAttributes(controllerAuthAttrs, roles);

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

    private static void CollectRolesFromAttributes(IEnumerable<AuthorizeAttribute> attributes, HashSet<string> roles)
    {
        foreach (var attr in attributes)
        {
            // Extract roles from Roles property
            if (!string.IsNullOrEmpty(attr.Roles))
            {
                foreach (var role in attr.Roles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    roles.Add(role);
                }
            }

            // Extract roles from Policy property via static mapping
            if (!string.IsNullOrEmpty(attr.Policy) && PolicyRoles.TryGetValue(attr.Policy, out var policyRoleList))
            {
                foreach (var role in policyRoleList)
                {
                    roles.Add(role);
                }
            }
        }
    }
}
