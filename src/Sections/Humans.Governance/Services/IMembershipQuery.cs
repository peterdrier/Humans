using Humans.Auth.Contracts;
using Humans.Base.Interfaces;
using Humans.Teams.Contracts;
using Humans.Base.Enums;

namespace Humans.Governance.Services;

/// <summary>
/// The slice of <see cref="ITeamService"/> and <see cref="IRoleAssignmentService"/> that
/// <see cref="MembershipCalculator"/> needs. Exists to break a DI cycle: both reach
/// <c>ISystemTeamSync</c>, whose implementation injects the calculator back.
/// </summary>
internal interface IMembershipQuery : IApplicationService
{
    /// <summary>
    /// Gets all teams the user is a member of, with the small amount of team
    /// metadata needed by membership calculations.
    /// </summary>
    Task<IReadOnlyList<MembershipTeamSnapshot>> GetUserTeamsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<bool> IsUserMemberOfTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true if the user has at least one active governance role
    /// assignment at the current instant.
    /// </summary>
    Task<bool> HasAnyActiveAssignmentAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the distinct set of user IDs that have at least one active
    /// governance role assignment at the current instant.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUserIdsWithActiveAssignmentsAsync(
        CancellationToken cancellationToken = default);
}

internal sealed record MembershipTeamSnapshot(
    Guid TeamId,
    TeamMemberRole Role,
    SystemTeamType TeamSystemTeamType);
