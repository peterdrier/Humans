namespace Humans.Teams.Contracts;

public static class TeamCoordinatorAccess
{
    /// <summary>Direct coordinator membership, a management role, or coordinator of the parent team.</summary>
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
