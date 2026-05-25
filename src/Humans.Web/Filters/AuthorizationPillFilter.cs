using System.Reflection;
using Humans.Domain.Constants;
using Humans.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Humans.Web.Filters;

/// <summary>
/// Sets ViewData["AuthPillRoles"] from action/controller [Authorize] role+policy attributes so the layout renders the auth pill.
/// </summary>
public class AuthorizationPillFilter : IActionFilter
{
    // Synthetic label for IsAnyTeamManagerOrCoordinator — display-only, not a real role.
    private const string TeamCoordinatorPillLabel = "TeamCoordinator";

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
        [RoleNames.EventsAdmin] = "Events Admin",
        [RoleNames.FeedbackAdmin] = "Feedback Admin",
        [RoleNames.HumanAdmin] = "Human Admin",
        [RoleNames.FinanceAdmin] = "Finance Admin",
        [TeamCoordinatorPillLabel] = "Team Coordinator",
        [RoleNames.StoreAdmin] = "Store Admin"
    };

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
        [PolicyNames.EventsAdminOrAdmin] = [RoleNames.EventsAdmin, RoleNames.Admin],
        [PolicyNames.StoreCatalogAdmin] = [RoleNames.StoreAdmin, RoleNames.FinanceAdmin, RoleNames.Admin],
        [PolicyNames.ReviewQueueAccess] = [RoleNames.ConsentCoordinator, RoleNames.VolunteerCoordinator, RoleNames.Board, RoleNames.Admin],
        [PolicyNames.ConsentCoordinatorBoardOrAdmin] = [RoleNames.ConsentCoordinator, RoleNames.Board, RoleNames.Admin],
        [PolicyNames.ShiftDashboardAccess] = [RoleNames.Admin, RoleNames.NoInfoAdmin, RoleNames.VolunteerCoordinator],
        // Admits team coordinators/sub-team managers via IsAnyTeamManagerOrCoordinatorRequirement.
        [PolicyNames.ShiftDepartmentManager] = [RoleNames.Admin, RoleNames.NoInfoAdmin, RoleNames.VolunteerCoordinator, TeamCoordinatorPillLabel],
        [PolicyNames.PrivilegedSignupApprover] = [RoleNames.Admin, RoleNames.NoInfoAdmin],
        [PolicyNames.VolunteerManager] = [RoleNames.Admin, RoleNames.VolunteerCoordinator],
        [PolicyNames.MedicalDataViewer] = [RoleNames.Admin, RoleNames.NoInfoAdmin],
    };

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated != true)
            return;

        if (context.ActionDescriptor is not ControllerActionDescriptor descriptor)
            return;

        if (descriptor.MethodInfo.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any())
            return;

        // Action-level [Authorize] OVERRIDES controller-level (narrower restriction wins) — otherwise pill misleads with the union.
        var roles = new HashSet<string>(StringComparer.Ordinal);

        var actionAuthAttrs = descriptor.MethodInfo
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .ToList();

        if (actionAuthAttrs.Count > 0)
        {
            CollectRolesFromAttributes(actionAuthAttrs, roles);
        }
        else
        {
            var controllerAuthAttrs = descriptor.ControllerTypeInfo
                .GetCustomAttributes<AuthorizeAttribute>(inherit: true);
            CollectRolesFromAttributes(controllerAuthAttrs, roles);
        }

        if (roles.Count == 0)
            return;

        // "Admin only" pill when Admin is the sole role.
        var hasAdmin = roles.Remove(RoleNames.Admin);
        if (roles.Count == 0)
        {
            if (hasAdmin && context.Controller is Controller adminController)
            {
                adminController.ViewData["AuthPillRoles"] = "Admin only";
            }
            return;
        }

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
    }

    private static void CollectRolesFromAttributes(IEnumerable<AuthorizeAttribute> attributes, HashSet<string> roles)
    {
        foreach (var attr in attributes)
        {
            if (!string.IsNullOrEmpty(attr.Roles))
            {
                foreach (var role in attr.Roles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    roles.Add(role);
                }
            }

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
