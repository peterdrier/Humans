using Humans.Application.Authorization;
using Humans.Domain.Constants;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

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
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        RoleAssignmentOperationRequirement requirement,
        string roleName)
    {
        if (RoleChecks.IsAdmin(context.User))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (SystemPrincipal.IsSystem(context.User))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (RoleChecks.IsBoard(context.User) || RoleChecks.IsHumanAdmin(context.User))
        {
            if (RoleChecks.CanManageRole(context.User, roleName))
            {
                context.Succeed(requirement);
            }
        }

        return Task.CompletedTask;
    }
}
