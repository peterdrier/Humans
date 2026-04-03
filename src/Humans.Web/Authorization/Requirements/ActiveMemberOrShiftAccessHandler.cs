using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Succeeds when the user has the ActiveMember claim, OR has TeamsAdmin/Board/Admin roles,
/// OR has shift dashboard access (Admin, NoInfoAdmin, VolunteerCoordinator).
/// </summary>
public class ActiveMemberOrShiftAccessHandler : AuthorizationHandler<ActiveMemberOrShiftAccessRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ActiveMemberOrShiftAccessRequirement requirement)
    {
        var user = context.User;

        if (user.HasClaim(RoleAssignmentClaimsTransformation.ActiveMemberClaimType,
                RoleAssignmentClaimsTransformation.ActiveClaimValue) ||
            RoleChecks.IsTeamsAdminBoardOrAdmin(user) ||
            ShiftRoleChecks.CanAccessDashboard(user))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
