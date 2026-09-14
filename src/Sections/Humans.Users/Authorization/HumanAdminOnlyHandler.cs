using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Users.Authorization;

internal sealed class HumanAdminOnlyHandler : AuthorizationHandler<HumanAdminOnlyRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        HumanAdminOnlyRequirement requirement)
    {
        var user = context.User;

        if (RoleChecks.IsHumanAdmin(user) && !RoleChecks.IsAdminOrBoard(user))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
