using Humans.Auth.Contracts;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Auth.Authorization;

/// <summary>Resource: the target role name. The rule is <see cref="SystemRoleRules.MayManage"/>.</summary>
internal sealed class SystemRoleAuthorizationHandler : AuthorizationHandler<SystemRoleOperationRequirement, string>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        SystemRoleOperationRequirement requirement,
        string roleName)
    {
        if (SystemRoleRules.MayManage(context.User, roleName))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
