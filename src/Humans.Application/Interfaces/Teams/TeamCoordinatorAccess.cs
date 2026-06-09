using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Teams;

/// <summary>
/// Shared coordinator-access rule over the Teams read model.
/// </summary>
public static class TeamCoordinatorAccess
{
    /// <summary>
    /// Returns true when the user can coordinate the active team through direct
    /// coordinator membership, a management role, or parent-team coordinator inheritance.
    /// </summary>
    public static bool IsCoordinatorOfActiveTeam(
        IReadOnlyDictionary<Guid, TeamInfo> teamsById,
        Guid teamId,
        Guid userId)
    {
        if (!teamsById.TryGetValue(teamId, out var team) || !team.IsActive)
            return false;

        if (team.Members.Any(member => member.UserId == userId && member.Role == TeamMemberRole.Coordinator))
            return true;

        if (!team.IsSystemTeam && team.ManagementRoleHolderUserIds?.Contains(userId) == true)
            return true;

        return team.ParentTeamId is Guid parentTeamId
            && IsCoordinatorOfActiveTeam(teamsById, parentTeamId, userId);
    }
}
