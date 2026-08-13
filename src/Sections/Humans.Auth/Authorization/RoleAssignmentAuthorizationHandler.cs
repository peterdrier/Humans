using Humans.Auth.Contracts;
using Humans.Domain.Constants;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Auth.Authorization;

/// <summary>
/// Resource-based authorization handler for role assignment operations.
/// - Admin: manage any role
/// - Board or HumanAdmin: manage roles in RoleNames.BoardManageableRoles
/// - Everyone else: deny
/// </summary>
internal sealed class RoleAssignmentAuthorizationHandler : AuthorizationHandler<RoleAssignmentOperationRequirement, string>
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
