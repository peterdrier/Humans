using System.Security.Claims;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Constants;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization handler for team management operations.
/// - Admin / TeamsAdmin / Board: allow any team, any requirement
/// - EETeamAdmin: allow any team for ManageEarlyEntry only (cross-team EE management)
/// - Team coordinator (or parent department coordinator): allow only their team
///   (covers both ManageCoordinators and ManageEarlyEntry — coordinators manage their
///   own team's early entry de facto)
/// - Everyone else: deny
/// </summary>
public class TeamAuthorizationHandler(ITeamServiceRead teamService)
    : AuthorizationHandler<TeamOperationRequirement, TeamInfo>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TeamOperationRequirement requirement,
        TeamInfo resource)
    {
        if (RoleChecks.IsTeamsAdminBoardOrAdmin(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        if (string.Equals(requirement.OperationName, TeamOperationRequirement.ManageEarlyEntry.OperationName, StringComparison.Ordinal)
            && context.User.IsInRole(RoleNames.EETeamAdmin))
        {
            context.Succeed(requirement);
            return;
        }

        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            return;

        var teamsById = await teamService.GetTeamsAsync();
        if (TeamCoordinatorAccess.IsCoordinatorOfActiveTeam(teamsById, resource.Id, userId))
        {
            context.Succeed(requirement);
        }
    }
}
