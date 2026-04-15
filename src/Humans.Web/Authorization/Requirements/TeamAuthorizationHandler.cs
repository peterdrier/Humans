using System.Security.Claims;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization handler for team management operations.
/// - Admin / TeamsAdmin: allow any team
/// - Team coordinator (or parent department coordinator): allow only their team
/// - Everyone else: deny
/// </summary>
public class TeamAuthorizationHandler : AuthorizationHandler<TeamOperationRequirement, Team>
{
    private readonly ITeamService _teamService;

    public TeamAuthorizationHandler(ITeamService teamService)
    {
        _teamService = teamService;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TeamOperationRequirement requirement,
        Team resource)
    {
        if (RoleChecks.IsTeamsAdminBoardOrAdmin(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            return;

        if (await _teamService.IsUserCoordinatorOfTeamAsync(resource.Id, userId))
        {
            context.Succeed(requirement);
        }
    }
}
