using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;

namespace Humans.Infrastructure.Services;

internal static class TeamResourceAccessRules
{
    public static async Task<bool> CanManageTeamResourcesAsync(
        ITeamService teamService,
        IRoleAssignmentService roleAssignmentService,
        TeamResourceManagementSettings resourceSettings,
        Guid teamId,
        Guid userId,
        CancellationToken ct = default)
    {
        if (await roleAssignmentService.IsUserBoardMemberAsync(userId, ct))
        {
            return true;
        }

        if (await roleAssignmentService.IsUserTeamsAdminAsync(userId, ct))
        {
            return true;
        }

        if (resourceSettings.AllowCoordinatorsToManageResources)
        {
            return await teamService.IsUserCoordinatorOfTeamAsync(teamId, userId, ct);
        }

        return false;
    }
}
