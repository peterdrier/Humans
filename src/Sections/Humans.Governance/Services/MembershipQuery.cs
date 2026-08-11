using Humans.Application.Interfaces.Auth;
using Humans.Governance.Contracts;
using Humans.Application.Interfaces.Teams;

namespace Humans.Governance.Services;

// Pass-through to ITeamServiceRead + IRoleAssignmentService. Exists to break the DI cycle
// MembershipCalculator → ITeamService → ISystemTeamSync → IMembershipCalculator.
internal sealed class MembershipQuery(ITeamServiceRead teamService, IRoleAssignmentService roleAssignmentService)
    : IMembershipQuery
{
    public async Task<IReadOnlyList<MembershipTeamSnapshot>> GetUserTeamsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return (await teamService.GetTeamsAsync(cancellationToken)).Values
            .SelectMany(t => t.Members
                .Where(m => m.UserId == userId)
                .Select(m => new MembershipTeamSnapshot(
                    t.Id,
                    m.Role,
                    t.SystemTeamType)))
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
