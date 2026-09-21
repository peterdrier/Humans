using System.Security.Claims;
using Humans.Base.Constants;
using Humans.CityPlanning.Contracts;
using Microsoft.AspNetCore.Authorization;

namespace Humans.CityPlanning.Authorization;

/// <summary>
/// Handler for <see cref="CityPlanningMapAdminRequirement"/>. Short-circuits for
/// CampAdmin/Admin, otherwise admits city-planning team members — the claim-free half of
/// the check, so it has to be a requirement rather than a role policy.
/// </summary>
internal sealed class CityPlanningMapAdminHandler(ICityPlanningServiceRead cityPlanning)
    : AuthorizationHandler<CityPlanningMapAdminRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CityPlanningMapAdminRequirement requirement)
    {
        var user = context.User;

        if (user.IsInRole(RoleNames.Admin) || user.IsInRole(RoleNames.CampAdmin))
        {
            context.Succeed(requirement);
            return;
        }

        var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            return;

        if (await cityPlanning.IsCityPlanningTeamMemberAsync(userId))
            context.Succeed(requirement);
    }
}
