using System.Reflection;
using Humans.Auth.Contracts;
using Humans.Base.Constants;
using Humans.Consent.Contracts;
using Humans.Finance.Contracts;
using Humans.Shifts.Contracts;
using Humans.Store.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Humans.Teams.Contracts;
using Humans.Camps.Contracts;
using Humans.Events.Contracts;
using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Humans.Web.Filters;

/// <summary>
/// Sets ViewData["AuthPillRoles"] from action/controller [Authorize] role+policy attributes so the layout renders the auth pill.
/// </summary>
public class AuthorizationPillFilter(RoleCatalog catalog) : IActionFilter
{
    // Synthetic label for IsAnyTeamManagerOrCoordinator — display-only, not a real role.
    private const string TeamCoordinatorPillLabel = "TeamCoordinator";

    private static readonly Dictionary<string, string[]> PolicyRoles = new(StringComparer.Ordinal)
    {
        [PolicyNames.AdminOnly] = [RoleNames.Admin],
        [PolicyNames.BoardOnly] = [RoleNames.Board],
        [PolicyNames.BoardOrAdmin] = [RoleNames.Board, RoleNames.Admin],
        [PolicyNames.HumanAdminBoardOrAdmin] = [UsersRoles.UsersAdmin, RoleNames.Board, RoleNames.Admin],
        [PolicyNames.HumanAdminOrAdmin] = [UsersRoles.UsersAdmin, RoleNames.Admin],
        [PolicyNames.TeamsAdminBoardOrAdmin] = [TeamsRoles.TeamsAdmin, RoleNames.Board, RoleNames.Admin],
        [PolicyNames.CampAdminOrAdmin] = [CampsRoles.CampsAdmin, RoleNames.Admin],
        // Admits team coordinators via CampComplianceAccessRequirement.
        [PolicyNames.CampComplianceAccess] = [CampsRoles.CampsAdmin, RoleNames.Admin, TeamCoordinatorPillLabel],
        [PolicyNames.TicketAdminBoardOrAdmin] = [TicketsRoles.TicketsAdmin, RoleNames.Admin, RoleNames.Board],
        [PolicyNames.TicketAdminOrAdmin] = [TicketsRoles.TicketsAdmin, RoleNames.Admin],
        [PolicyNames.FinanceAdminOrAdmin] = [FinanceRoles.FinanceAdmin, RoleNames.Admin],
        [PolicyNames.EventsAdminOrAdmin] = [EventsRoles.EventsAdmin, RoleNames.Admin],
        [PolicyNames.StoreCatalogAdmin] = [StoreRoles.StoreAdmin, FinanceRoles.FinanceAdmin, RoleNames.Admin],
        [PolicyNames.ReviewQueueAccess] = [ConsentRoles.ConsentCoordinator, ShiftsRoles.ShiftsAdmin, RoleNames.Board, RoleNames.Admin],
        [PolicyNames.ConsentCoordinatorBoardOrAdmin] = [ConsentRoles.ConsentCoordinator, RoleNames.Board, RoleNames.Admin],
        [PolicyNames.ShiftDashboardAccess] = [RoleNames.Admin, ShiftsRoles.ShiftsNoInfo, ShiftsRoles.ShiftsAdmin],
        // Admits team coordinators/sub-team managers via IsAnyTeamManagerOrCoordinatorRequirement.
        [PolicyNames.ShiftDepartmentManager] = [RoleNames.Admin, ShiftsRoles.ShiftsNoInfo, ShiftsRoles.ShiftsAdmin, TeamCoordinatorPillLabel],
        [PolicyNames.PrivilegedSignupApprover] = [RoleNames.Admin, ShiftsRoles.ShiftsNoInfo],
        [PolicyNames.VolunteerManager] = [RoleNames.Admin, ShiftsRoles.ShiftsAdmin],
        [PolicyNames.MedicalDataViewer] = [RoleNames.Admin, RoleNames.Board, ShiftsRoles.ShiftsNoInfo],
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
            .Select(r => string.Equals(r, TeamCoordinatorPillLabel, StringComparison.Ordinal) ? "Team Coordinator" : catalog.Title(r))
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
