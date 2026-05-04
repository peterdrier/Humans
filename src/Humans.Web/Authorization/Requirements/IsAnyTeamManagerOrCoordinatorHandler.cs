using System.Security.Claims;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Constants;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Succeeds when the user EITHER holds one of the privileged dashboard roles
/// (Admin, NoInfoAdmin, VolunteerCoordinator) OR is a coordinator / management
/// role-holder on any team or sub-team. Encodes the OR inside the handler so
/// <see cref="PolicyNames.ShiftDepartmentManager"/> can express role-or-team-coord
/// as a single requirement (multiple requirements on a policy AND together).
/// Reads the coordinator-team-ids list through <see cref="IShiftManagementService.GetCoordinatorTeamIdsAsync"/>
/// so it picks up the existing 60-second per-user cache (CacheKeys.ShiftAuthorization)
/// rather than hitting the DB on every request.
/// </summary>
public class IsAnyTeamManagerOrCoordinatorHandler : AuthorizationHandler<IsAnyTeamManagerOrCoordinatorRequirement>
{
    private readonly IShiftManagementService _shiftManagement;

    public IsAnyTeamManagerOrCoordinatorHandler(IShiftManagementService shiftManagement)
    {
        _shiftManagement = shiftManagement;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        IsAnyTeamManagerOrCoordinatorRequirement requirement)
    {
        var user = context.User;

        // Privileged-role short-circuit — same role list as ShiftDashboardAccess.
        // Avoids a service call (and its DB hit on cache miss) for the common
        // admin path.
        if (user.IsInRole(RoleNames.Admin)
            || user.IsInRole(RoleNames.NoInfoAdmin)
            || user.IsInRole(RoleNames.VolunteerCoordinator))
        {
            context.Succeed(requirement);
            return;
        }

        var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            return;

        var coordinatedTeamIds = await _shiftManagement.GetCoordinatorTeamIdsAsync(userId);
        if (coordinatedTeamIds.Count > 0)
            context.Succeed(requirement);
    }
}
