using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Teams;

namespace Humans.Application.Services.Notifications;

/// <summary>
/// Pass-through adapter delegating to <see cref="ITeamService"/> and
/// <see cref="IRoleAssignmentService"/>. Exists so <see cref="INotificationService"/>
/// doesn't depend on those services directly (they inject INotificationService,
/// which would close a DI cycle).
/// </summary>
public sealed class NotificationRecipientResolver(
    ITeamService teamService,
    IRoleAssignmentService roleAssignmentService) : INotificationRecipientResolver
{
    public async Task<TeamNotificationInfo?> GetTeamNotificationInfoAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        var team = await teamService.GetTeamAsync(teamId, cancellationToken);
        if (team is null)
        {
            return null;
        }

        var memberUserIds = team.Members.Select(m => m.UserId).ToList();
        return new TeamNotificationInfo(team.Id, team.Name, memberUserIds);
    }

    public Task<IReadOnlyList<Guid>> GetActiveUserIdsForRoleAsync(
        string roleName,
        CancellationToken cancellationToken = default) =>
        roleAssignmentService.GetActiveUserIdsInRoleAsync(roleName, cancellationToken);
}
