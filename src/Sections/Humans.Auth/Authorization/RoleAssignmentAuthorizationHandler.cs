using Humans.Auth.Contracts;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Auth.Authorization;

/// <summary>Resource: the target role name. The rule is <see cref="RoleAssignmentRules.MayManage"/>.</summary>
internal sealed class RoleAssignmentAuthorizationHandler : AuthorizationHandler<RoleAssignmentOperationRequirement, string>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        RoleAssignmentOperationRequirement requirement,
        string roleName)
    {
        if (RoleAssignmentRules.MayManage(context.User, roleName))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
