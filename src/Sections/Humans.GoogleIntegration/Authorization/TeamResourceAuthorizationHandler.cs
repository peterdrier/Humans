using System.Security.Claims;
using Humans.Base.Authorization;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Humans.GoogleIntegration.Authorization;

/// <summary>
/// TeamsAdmin and Admin manage any team; anyone else needs the manage check on the team's department, so sub-team
/// coordinators do not pass. Board reads the page through Teams' Read policy and manages nothing. Handlers get no token, so the read uses the request's cancellation.
/// </summary>
internal sealed class TeamResourceAuthorizationHandler(
    ITeamResourceService teamResources,
    IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<TeamResourceManageRequirement, TeamInfo>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TeamResourceManageRequirement requirement,
        TeamInfo resource)
    {
        if (TeamsRoles.IsTeamsAdmin(context.User) || RoleChecks.IsAdmin(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return;

        var departmentId = resource.ParentTeamId ?? resource.Id;
        if (await teamResources.CanManageTeamResourcesAsync(
                departmentId, userId, httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None))
            context.Succeed(requirement);
    }
}
