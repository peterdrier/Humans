using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Teams;

namespace Humans.Application.Services.Governance;

// Pass-through to ITeamService + IRoleAssignmentService. Exists to break the DI cycle
// MembershipCalculator → ITeamService → ISystemTeamSync → IMembershipCalculator.
public sealed class MembershipQuery(ITeamService teamService, IRoleAssignmentService roleAssignmentService)
    : IMembershipQuery
{
    public async Task<IReadOnlyList<MembershipTeamSnapshot>> GetUserTeamsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var memberships = await teamService.GetUserTeamsAsync(userId, cancellationToken);
#pragma warning disable CS0618 // TeamMember.Team nav read is included on this path; stitching off UserInfo would be a layer-skip.
        return memberships
            .Select(m => new MembershipTeamSnapshot(
                m.TeamId,
                m.Role,
                m.Team.SystemTeamType))
            .ToList();
#pragma warning restore CS0618
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
