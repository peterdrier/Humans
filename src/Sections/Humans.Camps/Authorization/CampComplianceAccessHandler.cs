using Humans.Camps.Contracts;
using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Shifts.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Humans.Camps.Authorization;

internal sealed class CampComplianceAccessHandler(
    IShiftService shiftManagement, IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<CampComplianceAccessRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CampComplianceAccessRequirement requirement)
    {
        var user = context.User;

        if (user.IsInRole(RoleNames.Admin) || user.IsInRole(RoleNames.Board) || user.IsInRole(CampsRoles.CampsAdmin))
        {
            context.Succeed(requirement);
            return;
        }

        var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            return;

        var coordinatedTeamIds = await shiftManagement.GetCoordinatorTeamIdsAsync(
            userId, httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None);
        if (coordinatedTeamIds.Count > 0)
            context.Succeed(requirement);
    }
}
