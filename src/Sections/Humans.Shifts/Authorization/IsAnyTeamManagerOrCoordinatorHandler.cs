using System.Security.Claims;
using Humans.Shifts.Contracts;
using Humans.Base.Constants;
using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Shifts.Authorization;

/// <summary>
/// A privileged dashboard role OR coordinator of any team, in one handler because a policy's requirements AND.
/// Coordinator team ids come from Teams' in-RAM model, so no request hits the DB.
/// </summary>
internal sealed class IsAnyTeamManagerOrCoordinatorHandler(IShiftService shiftManagement)
    : AuthorizationHandler<IsAnyTeamManagerOrCoordinatorRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        IsAnyTeamManagerOrCoordinatorRequirement requirement)
    {
        var user = context.User;

        // Same role list as ShiftDashboardAccess; spares the common admin path a service call.
        if (user.IsInRole(RoleNames.Admin)
            || user.IsInRole(ShiftsRoles.ShiftsNoInfo)
            || user.IsInRole(ShiftsRoles.ShiftsAdmin))
        {
            context.Succeed(requirement);
            return;
        }

        var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            return;

        var coordinatedTeamIds = await shiftManagement.GetCoordinatorTeamIdsAsync(userId);
        if (coordinatedTeamIds.Count > 0)
            context.Succeed(requirement);
    }
}
