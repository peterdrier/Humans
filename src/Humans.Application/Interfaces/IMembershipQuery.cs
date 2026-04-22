using Humans.Domain.Entities;

namespace Humans.Application.Interfaces;

/// <summary>
/// Thin read-only query surface exposing ONLY the subset of
/// <see cref="ITeamService"/> and <see cref="IRoleAssignmentService"/> methods
/// consumed by <see cref="IMembershipCalculator"/>.
/// </summary>
/// <remarks>
/// <para>
/// Exists to break a circular DI graph: <see cref="ITeamService"/> and
/// <see cref="IRoleAssignmentService"/> both inject <c>ISystemTeamSync</c>,
/// whose implementation (<c>SystemTeamSyncJob</c>) injects
/// <see cref="IMembershipCalculator"/> back. Injecting the full team / role
/// services into the calculator closes that cycle and trips
/// <c>ValidateOnBuild</c>.
/// </para>
/// <para>
/// The query adapter depends on the team and role services, but nothing
/// injects the adapter except <see cref="IMembershipCalculator"/> — so no
/// cycle. Same pattern as <see cref="INotificationRecipientResolver"/>.
/// </para>
/// </remarks>
public interface IMembershipQuery
{
    /// <summary>
    /// Gets all teams the user is a member of (with <c>Team</c> navigation
    /// populated so callers can inspect <c>SystemTeamType</c>).
    /// </summary>
    Task<IReadOnlyList<TeamMember>> GetUserTeamsAsync(
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
