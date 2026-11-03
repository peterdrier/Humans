using Humans.EarlyEntry.Contracts;
using Humans.Base.Authorization;
using System.Security.Claims;
using Humans.Teams.Contracts;
using Humans.Base.Constants;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Teams.Authorization;

/// <summary>
/// Admin and TeamsAdmin: any team, any requirement. EarlyEntryAdmin: any team, ManageEarlyEntry only. A coordinator
/// of the team or its parent department: that team, both requirements. Everyone else, Board included: deny. Board
/// reads the per-team admin pages through Teams' Read policy.
/// </summary>
internal sealed class TeamAuthorizationHandler(ITeamService teamService)
    : AuthorizationHandler<TeamOperationRequirement, TeamInfo>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TeamOperationRequirement requirement,
        TeamInfo resource)
    {
        if (TeamsRoles.IsTeamsAdmin(context.User) || RoleChecks.IsAdmin(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        if (string.Equals(requirement.OperationName, TeamOperationRequirement.ManageEarlyEntry.OperationName, StringComparison.Ordinal)
            && context.User.IsInRole(EarlyEntryRoles.EarlyEntryAdmin))
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
