using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Succeeds when the user has the ActiveMember claim OR has TeamsAdmin/Board/Admin roles.
/// </summary>
public class IsActiveMemberHandler : AuthorizationHandler<IsActiveMemberRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        IsActiveMemberRequirement requirement)
    {
        var user = context.User;

        if (user.HasClaim(RoleAssignmentClaimsTransformation.ActiveMemberClaimType,
                RoleAssignmentClaimsTransformation.ActiveClaimValue) ||
            RoleChecks.IsTeamsAdminBoardOrAdmin(user))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
