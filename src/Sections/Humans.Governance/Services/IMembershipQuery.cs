using Humans.Auth.Contracts;
using Humans.Base.Interfaces;
using Humans.Teams.Contracts;
using Humans.Base.Enums;

namespace Humans.Governance.Services;

/// <summary>
/// Thin read-only query surface exposing ONLY the subset of
/// <see cref="ITeamServiceRead"/> and <see cref="IRoleAssignmentService"/>
/// methods consumed by <see cref="MembershipCalculator"/>.
/// </summary>
/// <remarks>
/// <para>
/// Exists to break a circular DI graph: <see cref="ITeamServiceRead"/> and
/// <see cref="IRoleAssignmentService"/> both inject <c>ISystemTeamSync</c>,
/// whose implementation (<c>SystemTeamSyncJob</c>) injects
/// <see cref="Humans.Governance.Contracts.IMembershipCalculatorRead"/> back. Injecting the full team / role
/// services into the calculator closes that cycle and trips
/// <c>ValidateOnBuild</c>.
/// </para>
/// </remarks>
internal interface IMembershipQuery : IApplicationService
{
    /// <summary>
    /// Gets all teams the user is a member of, with the small amount of team
    /// metadata needed by membership calculations.
    /// </summary>
    Task<IReadOnlyList<MembershipTeamSnapshot>> GetUserTeamsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a user is a member of a team.
    /// </summary>
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
