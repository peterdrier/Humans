using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Teams;

namespace Humans.Application.Services.Governance;

// Pass-through to ITeamServiceRead + IRoleAssignmentService. Exists to break the DI cycle
// MembershipCalculator → ITeamService → ISystemTeamSync → IMembershipCalculator.
public sealed class MembershipQuery(ITeamServiceRead teamService, IRoleAssignmentService roleAssignmentService)
    : IMembershipQuery
{
    public async Task<IReadOnlyList<MembershipTeamSnapshot>> GetUserTeamsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return (await teamService.GetTeamsAsync(cancellationToken)).Values
            .Select(t => new { TeamInfo = t, Membership = t.Members.FirstOrDefault(m => m.UserId == userId) })
            .Where(x => x.Membership is not null)
            .Select(x => new MembershipTeamSnapshot(
                x.TeamInfo.Id,
                x.Membership!.Role,
                x.TeamInfo.SystemTeamType))
            .ToList();
    }

    public async Task<bool> IsUserMemberOfTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var t = await teamService.GetTeamAsync(teamId, cancellationToken);
        return t is { IsActive: true } && t.Members.Any(m => m.UserId == userId);
    }

    public Task<bool> HasAnyActiveAssignmentAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        roleAssignmentService.HasAnyActiveAssignmentAsync(userId, cancellationToken);

    public Task<IReadOnlyList<Guid>> GetUserIdsWithActiveAssignmentsAsync(
        CancellationToken cancellationToken = default) =>
        roleAssignmentService.GetUserIdsWithActiveAssignmentsAsync(cancellationToken);
}
