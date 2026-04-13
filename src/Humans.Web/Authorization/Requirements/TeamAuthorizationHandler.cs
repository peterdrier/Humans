using System.Security.Claims;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization handler for team management operations.
/// Evaluates whether a user can perform coordinator/management operations on a specific Team.
///
/// Authorization logic:
/// - Admin: allow any team
/// - TeamsAdmin: allow any team
/// - Team coordinator (or parent department coordinator): allow only their team
/// - Everyone else: deny
///
/// Uses IServiceProvider to lazily resolve ITeamService, breaking the DI cycle:
/// TeamService → RoleAssignmentService → IAuthorizationService → TeamAuthorizationHandler → ITeamService
/// </summary>
public class TeamAuthorizationHandler : AuthorizationHandler<TeamOperationRequirement, Team>
{
    private readonly IServiceProvider _serviceProvider;

    public TeamAuthorizationHandler(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TeamOperationRequirement requirement,
        Team resource)
    {
        // Admin and TeamsAdmin can manage any team
        if (RoleChecks.IsTeamsAdminBoardOrAdmin(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        // Check if user is a coordinator for this specific team (or its parent department)
        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            return;

        var teamService = _serviceProvider.GetRequiredService<ITeamService>();
        if (await teamService.IsUserCoordinatorOfTeamAsync(resource.Id, userId))
        {
            context.Succeed(requirement);
        }
    }
}
