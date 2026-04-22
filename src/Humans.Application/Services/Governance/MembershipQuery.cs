using Humans.Application.Interfaces;
using Humans.Domain.Entities;

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

    public Task<IReadOnlyList<TeamMember>> GetUserTeamsAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        _teamService.GetUserTeamsAsync(userId, cancellationToken);

    public Task<bool> IsUserMemberOfTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default) =>
        _teamService.IsUserMemberOfTeamAsync(teamId, userId, cancellationToken);

    public Task<bool> HasAnyActiveAssignmentAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        _roleAssignmentService.HasAnyActiveAssignmentAsync(userId, cancellationToken);

    public Task<IReadOnlyList<Guid>> GetUserIdsWithActiveAssignmentsAsync(
        CancellationToken cancellationToken = default) =>
        _roleAssignmentService.GetUserIdsWithActiveAssignmentsAsync(cancellationToken);
}
