using Humans.Users.Contracts;
using System.Security.Claims;
using Humans.Shifts.Contracts;
using Humans.Base.Constants;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Shifts.Authorization;

/// <summary>
/// A privileged dashboard role OR <see cref="ShiftsPermissions.Manage"/> or <see cref="ShiftsPermissions.Approve"/>
/// on any team, in one handler because a policy's requirements AND.
/// </summary>
internal sealed class IsAnyTeamManagerOrCoordinatorHandler(
    IShiftService shiftManagement, IUserService users, IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<IsAnyTeamManagerOrCoordinatorRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        IsAnyTeamManagerOrCoordinatorRequirement requirement)
    {
        var user = context.User;

        // Same role list as ShiftsPolicies.Read; spares the common admin path a service call.
        if (user.IsInRole(RoleNames.Admin)
            || user.IsInRole(RoleNames.Board)
            || user.IsInRole(ShiftsRoles.ShiftsNoInfo)
            || user.IsInRole(ShiftsRoles.ShiftsAdmin))
        {
            context.Succeed(requirement);
            return;
        }

        var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            return;

        var ct = httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;
        var viewer = await users.GetUserInfoAsync(userId, ct);
        if (viewer is null) return;
        if (await shiftManagement.CanManageOrApproveAnyShiftsAsync(viewer.Id, ct))
            context.Succeed(requirement);
    }
}
