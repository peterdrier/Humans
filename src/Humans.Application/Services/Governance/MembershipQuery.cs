using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Teams;

namespace Humans.Application.Services.Governance;

/// <summary>
/// Default <see cref="IMembershipQuery"/> implementation. Sealed
/// pass-through that delegates to <see cref="ITeamService"/> and
/// <see cref="IRoleAssignmentService"/>.
/// </summary>
/// <remarks>
/// Holds no state and applies no business logic. Exists solely to keep
/// <see cref="IMembershipCalculator"/> from depending on the team and
/// role-assignment services directly — those services pull in
/// <c>ISystemTeamSync</c>, whose implementation injects the calculator,
/// closing the DI cycle. See <see cref="IMembershipQuery"/> remarks.
/// </remarks>
public sealed class MembershipQuery : IMembershipQuery
{
    private readonly ITeamService _teamService;
    private readonly IRoleAssignmentService _roleAssignmentService;

    public MembershipQuery(
        ITeamService teamService,
        IRoleAssignmentService roleAssignmentService)
    {
        _teamService = teamService;
        _roleAssignmentService = roleAssignmentService;
    }

    public async Task<IReadOnlyList<MembershipTeamSnapshot>> GetUserTeamsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var memberships = await _teamService.GetUserTeamsAsync(userId, cancellationToken);
#pragma warning disable CS0618 // Cross-domain nav read: TeamMember.Team is included on this read path; stitching off the cached UserInfo here would be a layer-skip.
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
        var t = await _teamService.GetTeamAsync(teamId, cancellationToken);
        return t is { IsActive: true } && t.Members.Any(m => m.UserId == userId);
    }

    public Task<bool> HasAnyActiveAssignmentAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        _roleAssignmentService.HasAnyActiveAssignmentAsync(userId, cancellationToken);

    public Task<IReadOnlyList<Guid>> GetUserIdsWithActiveAssignmentsAsync(
        CancellationToken cancellationToken = default) =>
        _roleAssignmentService.GetUserIdsWithActiveAssignmentsAsync(cancellationToken);
}
