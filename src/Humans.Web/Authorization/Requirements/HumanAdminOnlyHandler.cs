using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Succeeds when the user has HumanAdmin role but is NOT Admin or Board.
/// </summary>
public class HumanAdminOnlyHandler : AuthorizationHandler<HumanAdminOnlyRequirement>
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
