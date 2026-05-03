using System.Security.Claims;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Constants;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Succeeds when the user EITHER holds one of the privileged dashboard roles
/// (Admin, NoInfoAdmin, VolunteerCoordinator) OR is a coordinator / management
/// role-holder on any team or sub-team. Encodes the OR inside the handler so
/// <see cref="PolicyNames.ShiftDepartmentManager"/> can express role-or-team-coord
/// as a single requirement (multiple requirements on a policy AND together).
/// </summary>
public class IsAnyTeamCoordinatorHandler : AuthorizationHandler<IsAnyTeamCoordinatorRequirement>
{
    private readonly ITeamService _teamService;

    public IsAnyTeamCoordinatorHandler(ITeamService teamService)
    {
        _teamService = teamService;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        IsAnyTeamCoordinatorRequirement requirement)
    {
        var user = context.User;

        // Privileged-role short-circuit — same role list as ShiftDashboardAccess.
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

        var coordinatedTeamIds = await _teamService.GetUserCoordinatedTeamIdsAsync(userId);
        if (coordinatedTeamIds.Count > 0)
            context.Succeed(requirement);
    }
}
