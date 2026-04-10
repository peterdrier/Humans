using Humans.Domain.Constants;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Application.Authorization;

/// <summary>
/// Resource-based authorization handler for role assignment operations.
/// Evaluates whether a user can assign or end a specific role.
///
/// Authorization logic:
/// - Admin: can manage any role
/// - Board or HumanAdmin: can manage roles in RoleNames.BoardManageableRoles
/// - System principal: can manage any role (for background jobs)
/// - Everyone else: deny
/// </summary>
public class RoleAssignmentAuthorizationHandler : AuthorizationHandler<RoleAssignmentOperationRequirement, string>
{
    private static IReadOnlySet<string> BoardManageableRoles => RoleNames.BoardManageableRoles;

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
