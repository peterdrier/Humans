using Profiles.Domain.Entities;
using Profiles.Domain.Enums;

namespace Profiles.Application.Interfaces;

/// <summary>
/// Service for managing teams and team membership.
/// </summary>
public interface ITeamService
{
    /// <summary>
    /// Creates a new team.
    /// </summary>
    Task<Team> CreateTeamAsync(
        string name,
        string? description,
        bool requiresApproval,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a team by its slug.
    /// </summary>
    Task<Team?> GetTeamBySlugAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a team by its ID.
    /// </summary>
    Task<Team?> GetTeamByIdAsync(Guid teamId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all active teams.
    /// </summary>
    Task<IReadOnlyList<Team>> GetAllTeamsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all user-created (non-system) teams.
    /// </summary>
    Task<IReadOnlyList<Team>> GetUserCreatedTeamsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all teams the user is a member of.
    /// </summary>
    Task<IReadOnlyList<TeamMember>> GetUserTeamsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a team's details.
    /// </summary>
    Task<Team> UpdateTeamAsync(
        Guid teamId,
        string name,
        string? description,
        bool requiresApproval,
        bool isActive,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes (deactivates) a team.
    /// </summary>
    Task DeleteTeamAsync(Guid teamId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests to join a team (for teams that require approval).
    /// </summary>
    Task<TeamJoinRequest> RequestToJoinTeamAsync(
        Guid teamId,
        Guid userId,
        string? message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Joins a team directly (for teams that don't require approval).
    /// </summary>
    Task<TeamMember> JoinTeamDirectlyAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Leaves a team.
    /// </summary>
    Task LeaveTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Withdraws a pending join request.
    /// </summary>
    Task WithdrawJoinRequestAsync(
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves a join request.
    /// </summary>
    Task<TeamMember> ApproveJoinRequestAsync(
        Guid requestId,
        Guid approverUserId,
        string? notes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rejects a join request.
    /// </summary>
    Task RejectJoinRequestAsync(
        Guid requestId,
        Guid approverUserId,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all pending join requests for teams the user can approve.
    /// </summary>
    Task<IReadOnlyList<TeamJoinRequest>> GetPendingRequestsForApproverAsync(
        Guid approverUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets pending join requests for a specific team.
    /// </summary>
    Task<IReadOnlyList<TeamJoinRequest>> GetPendingRequestsForTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a user's pending request for a team, if any.
    /// </summary>
    Task<TeamJoinRequest?> GetUserPendingRequestAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a user can approve requests for a team.
    /// </summary>
    Task<bool> CanUserApproveRequestsForTeamAsync(
        Guid teamId,
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
    /// Checks if a user is a metalead of a team.
    /// </summary>
    Task<bool> IsUserMetaleadOfTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a user is a board member (has active Board RoleAssignment).
    /// </summary>
    Task<bool> IsUserBoardMemberAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a member's role within a team.
    /// </summary>
    Task SetMemberRoleAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a member from a team (admin action).
    /// </summary>
    Task RemoveMemberAsync(
        Guid teamId,
        Guid userId,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all members of a team.
    /// </summary>
    Task<IReadOnlyList<TeamMember>> GetTeamMembersAsync(
        Guid teamId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets pending request counts for multiple teams in a single query.
    /// </summary>
    /// <param name="teamIds">The team IDs to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dictionary mapping team ID to pending request count.</returns>
    Task<IReadOnlyDictionary<Guid, int>> GetPendingRequestCountsByTeamIdsAsync(
        IEnumerable<Guid> teamIds,
        CancellationToken cancellationToken = default);
}
