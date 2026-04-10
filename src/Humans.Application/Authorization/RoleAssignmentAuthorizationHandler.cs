using Humans.Domain.Constants;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Application.Authorization;

/// <summary>
/// Resource-based authorization handler for role assignment operations.
/// Evaluates whether a user can assign or end a specific role.
///
/// Authorization logic:
/// - Admin: can manage any role
/// - Board or HumanAdmin: can manage Board, HumanAdmin, TeamsAdmin, CampAdmin,
///   TicketAdmin, NoInfoAdmin, FeedbackAdmin, FinanceAdmin, ConsentCoordinator,
///   and VolunteerCoordinator
/// - System principal: can manage any role (for background jobs)
/// - Everyone else: deny
/// </summary>
public class RoleAssignmentAuthorizationHandler : AuthorizationHandler<RoleAssignmentOperationRequirement, string>
{
    /// <summary>
    /// Roles that Board and HumanAdmin are permitted to manage.
    /// Must match BoardAssignableRoles in the Web layer's RoleChecks.
    /// </summary>
    private static readonly HashSet<string> BoardManageableRoles = new(StringComparer.Ordinal)
    {
        RoleNames.Board,
        RoleNames.HumanAdmin,
        RoleNames.TeamsAdmin,
        RoleNames.CampAdmin,
        RoleNames.TicketAdmin,
        RoleNames.NoInfoAdmin,
        RoleNames.FeedbackAdmin,
        RoleNames.FinanceAdmin,
        RoleNames.ConsentCoordinator,
        RoleNames.VolunteerCoordinator
    };

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        RoleAssignmentOperationRequirement requirement,
        string roleName)
    {
        if (context.User.IsInRole(RoleNames.Admin))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (SystemPrincipal.IsSystem(context.User))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (context.User.IsInRole(RoleNames.Board) || context.User.IsInRole(RoleNames.HumanAdmin))
        {
            if (BoardManageableRoles.Contains(roleName))
            {
                context.Succeed(requirement);
            }
        }

        return Task.CompletedTask;
    }
}
