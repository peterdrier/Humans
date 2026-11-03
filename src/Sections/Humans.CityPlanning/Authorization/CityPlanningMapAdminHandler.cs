using Humans.Camps.Contracts;
using System.Security.Claims;
using Humans.Base.Constants;
using Humans.CityPlanning.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Humans.CityPlanning.Authorization;

/// <summary>
/// CampAdmin/Admin, else city-planning team members. Team membership is not a claim, so this has to be a requirement
/// rather than a role policy.
/// </summary>
internal sealed class CityPlanningMapAdminHandler(ICityPlanningService cityPlanning, IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<CityPlanningMapAdminRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CityPlanningMapAdminRequirement requirement)
    {
        var user = context.User;

        if (user.IsInRole(RoleNames.Admin) || user.IsInRole(CampsRoles.CampsAdmin))
        {
            context.Succeed(requirement);
            return;
        }

        var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            return;

        if (await cityPlanning.IsCityPlanningTeamMemberAsync(
                userId, httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None))
            context.Succeed(requirement);
    }
}
