using System.Security.Claims;
using Humans.Base.Authorization;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
using Microsoft.AspNetCore.Authorization;

namespace Humans.GoogleIntegration.Authorization;

/// <summary>
/// Resource-based handler for <see cref="TeamResourceManageRequirement"/> on a <c>TeamInfo</c>.
/// TeamsAdmin, Board and Admin manage any team. Everyone else needs
/// <see cref="ITeamResourceService.CanManageTeamResourcesAsync"/> on the team's department:
/// a sub-team is checked at its parent, so sub-team coordinators do not pass.
/// Deny-by-default: only the explicit Succeed paths grant access.
/// </summary>
internal sealed class TeamResourceAuthorizationHandler(ITeamResourceService teamResources)
    : AuthorizationHandler<TeamResourceManageRequirement, TeamInfo>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TeamResourceManageRequirement requirement,
        TeamInfo resource)
    {
        if (RoleChecks.IsTeamsAdminBoardOrAdmin(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return;

        var departmentId = resource.ParentTeamId ?? resource.Id;
        if (await teamResources.CanManageTeamResourcesAsync(departmentId, userId))
            context.Succeed(requirement);
    }
}
