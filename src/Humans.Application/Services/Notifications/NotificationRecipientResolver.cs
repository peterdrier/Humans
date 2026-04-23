using Humans.Application.Interfaces;

namespace Humans.Application.Services.Notifications;

/// <summary>
/// Default <see cref="INotificationRecipientResolver"/> implementation that
/// delegates to <see cref="ITeamService"/> and
/// <see cref="IRoleAssignmentService"/>.
/// </summary>
/// <remarks>
/// Pass-through adapter — holds no state and applies no business logic beyond
/// projecting team + members into <see cref="TeamNotificationInfo"/>. Exists
/// solely to keep <see cref="INotificationService"/> from depending on the
/// team / role-assignment services directly (those services inject
/// <see cref="INotificationService"/> in the other direction, which would
/// otherwise close a cycle).
/// </remarks>
public sealed class NotificationRecipientResolver : INotificationRecipientResolver
{
    private readonly ITeamService _teamService;
    private readonly IRoleAssignmentService _roleAssignmentService;

    public NotificationRecipientResolver(
        ITeamService teamService,
        IRoleAssignmentService roleAssignmentService)
    {
        _teamService = teamService;
        _roleAssignmentService = roleAssignmentService;
    }

    public async Task<TeamNotificationInfo?> GetTeamNotificationInfoAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        var team = await _teamService.GetTeamByIdAsync(teamId, cancellationToken);
        if (team is null)
        {
            return null;
        }

        var members = await _teamService.GetTeamMembersAsync(teamId, cancellationToken);
        var memberUserIds = members.Select(m => m.UserId).ToList();
        return new TeamNotificationInfo(team.Id, team.Name, memberUserIds);
    }

    public Task<IReadOnlyList<Guid>> GetActiveUserIdsForRoleAsync(
        string roleName,
        CancellationToken cancellationToken = default) =>
        _roleAssignmentService.GetActiveUserIdsInRoleAsync(roleName, cancellationToken);
}
