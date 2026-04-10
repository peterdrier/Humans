using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using NodaTime;

namespace Humans.Application.Interfaces;

public record CachedTeam(
    Guid Id, string Name, string? Description, string Slug,
    bool IsSystemTeam, SystemTeamType SystemTeamType, bool RequiresApproval,
    bool IsPublicPage, bool IsHidden, Instant CreatedAt, List<CachedTeamMember> Members,
    Guid? ParentTeamId = null);

public record CachedTeamMember(
    Guid TeamMemberId, Guid UserId, string DisplayName,
    string? ProfilePictureUrl, TeamMemberRole Role, Instant JoinedAt);

public record TeamDirectorySummary(
    Guid Id,
    string Name,
    string? Description,
    string Slug,
    int MemberCount,
    bool IsSystemTeam,
    bool RequiresApproval,
    bool IsPublicPage,
    bool IsCurrentUserMember,
    bool IsCurrentUserCoordinator,
    string? ParentTeamName,
    string? ParentTeamSlug)
{
    public string SortKey => ParentTeamName is not null ? $"{ParentTeamName} - {Name}" : Name;
}

public record TeamDirectoryResult(
    bool IsAuthenticated,
    bool CanCreateTeam,
    IReadOnlyList<TeamDirectorySummary> MyTeams,
    IReadOnlyList<TeamDirectorySummary> Departments,
    IReadOnlyList<TeamDirectorySummary> SystemTeams);

public record TeamDetailMemberSummary(
    Guid UserId,
    string DisplayName,
    string? Email,
    string? ProfilePictureUrl,
    TeamMemberRole Role,
    Instant JoinedAt);

public record TeamDetailResult(
    Team Team,
    IReadOnlyList<TeamDetailMemberSummary> Members,
    IReadOnlyList<Team> ChildTeams,
    IReadOnlyList<TeamRoleDefinition> RoleDefinitions,
    bool IsAuthenticated,
    bool IsCurrentUserMember,
    bool IsCurrentUserCoordinator,
    bool CanCurrentUserJoin,
    bool CanCurrentUserLeave,
    bool CanCurrentUserManage,
    bool CanCurrentUserEditTeam,
    Guid? CurrentUserPendingRequestId,
    int PendingRequestCount);

public record MyTeamMembershipSummary(
    Guid TeamId,
    string TeamName,
    string TeamSlug,
    bool IsSystemTeam,
    TeamMemberRole Role,
    Instant JoinedAt,
    bool CanLeave,
    int PendingRequestCount);

public record UserTeamGoogleResource(
    string TeamName,
    string TeamSlug,
    string ResourceName,
    GoogleResourceType ResourceType,
    string? Url);

public record TeamRosterSlotSummary(
    string TeamName,
    string TeamSlug,
    string RoleName,
    string? RoleDescription,
    Guid RoleDefinitionId,
    int SlotNumber,
    string Priority,
    string PriorityBadgeClass,
    string Period,
    bool IsFilled,
    Guid? AssignedUserId,
    string? AssignedUserName);

public record TeamOptionDto(Guid Id, string Name);

public record AdminTeamSummary(
    Guid Id,
    string Name,
    string Slug,
    bool IsActive,
    bool RequiresApproval,
    bool IsSystemTeam,
    string? SystemTeamType,
    int MemberCount,
    int PendingRequestCount,
    bool HasMailGroup,
    string? GoogleGroupEmail,
    int DriveResourceCount,
    int RoleSlotCount,
    Instant CreatedAt,
    bool IsChildTeam,
    int PendingShiftSignupCount,
    bool IsHidden);

public record AdminTeamListResult(
    IReadOnlyList<AdminTeamSummary> Teams,
    int TotalCount);

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
        Guid? parentTeamId = null,
        string? googleGroupPrefix = null,
        bool isHidden = false,
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
    /// Gets the summarized team directory for anonymous or authenticated viewers.
    /// </summary>
    Task<TeamDirectoryResult> GetTeamDirectoryAsync(Guid? userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the detail-page data for a visible team, including viewer-specific membership and management state.
    /// Returns null when the team does not exist or is not visible to the viewer.
    /// </summary>
    Task<TeamDetailResult?> GetTeamDetailAsync(string slug, Guid? userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all teams the user is a member of.
    /// </summary>
    Task<IReadOnlyList<TeamMember>> GetUserTeamsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets active Google resources grouped by team for a user's active team memberships.
    /// Used by the MyGoogleResources view component on the dashboard.
    /// </summary>
    Task<IReadOnlyList<UserTeamGoogleResource>> GetUserTeamGoogleResourcesAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current user's team memberships with viewer-specific pending-request counts.
    /// </summary>
    Task<IReadOnlyList<MyTeamMembershipSummary>> GetMyTeamMembershipsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a team's details.
    /// </summary>
    Task<Team> UpdateTeamAsync(
        Guid teamId,
        string name,
        string? description,
        bool requiresApproval,
        bool isActive,
        Guid? parentTeamId = null,
        string? googleGroupPrefix = null,
        string? customSlug = null,
        bool? hasBudget = null,
        bool? isHidden = null,
        bool? isSensitive = null,
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
    Task<bool> LeaveTeamAsync(
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
    /// Checks if a user is a coordinator of a team.
    /// </summary>
    Task<bool> IsUserCoordinatorOfTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a member from a team (admin action).
    /// </summary>
    Task<bool> RemoveMemberAsync(
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

    /// <summary>
    /// Gets the management role definition name for each team that has one.
    /// </summary>
    /// <param name="teamIds">The team IDs to check.</param>
    /// <returns>Dictionary mapping team ID to the management role name.</returns>
    Task<IReadOnlyDictionary<Guid, string>> GetManagementRoleNamesByTeamIdsAsync(
        IEnumerable<Guid> teamIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets non-system team names for users, grouped by user ID.
    /// Used for birthday display.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, List<string>>> GetNonSystemTeamNamesByUserIdsAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets active teams as lightweight Id+Name options for dropdown lists.
    /// </summary>
    Task<IReadOnlyList<TeamOptionDto>> GetActiveTeamOptionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all teams for admin list with active member counts and pending request counts.
    /// </summary>
    Task<(IReadOnlyList<Team> Items, int TotalCount)> GetAllTeamsForAdminAsync(
        int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets ordered admin-team summaries ready for controller/view projection.
    /// </summary>
    Task<AdminTeamListResult> GetAdminTeamListAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the public roster summary with optional filters applied.
    /// </summary>
    Task<IReadOnlyList<TeamRosterSlotSummary>> GetRosterAsync(
        string? priority,
        string? status,
        string? period,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Directly adds a user to a team (admin/lead action, bypasses join request workflow).
    /// </summary>
    Task<TeamMember> AddMemberToTeamAsync(
        Guid teamId,
        Guid targetUserId,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    // ==========================================================================
    // Team Page Content
    // ==========================================================================

    /// <summary>
    /// Updates a team's public page content, CTAs, and visibility.
    /// </summary>
    Task UpdateTeamPageContentAsync(
        Guid teamId,
        string? pageContent,
        List<CallToAction> callsToAction,
        bool isPublicPage,
        bool showCoordinatorsOnPublicPage,
        Guid updatedByUserId,
        CancellationToken cancellationToken = default);

    // ==========================================================================
    // Team Role Definitions
    // ==========================================================================

    /// <summary>
    /// Creates a new role definition for a team.
    /// </summary>
    Task<TeamRoleDefinition> CreateRoleDefinitionAsync(
        Guid teamId, string name, string? description, int slotCount,
        List<SlotPriority> priorities, int sortOrder, RolePeriod period, Guid actorUserId,
        bool isPublic = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing role definition.
    /// </summary>
    Task<TeamRoleDefinition> UpdateRoleDefinitionAsync(
        Guid roleDefinitionId, string name, string? description, int slotCount,
        List<SlotPriority> priorities, int sortOrder, bool isManagement, RolePeriod period, Guid actorUserId,
        bool isPublic = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a role definition and its assignments.
    /// </summary>
    Task DeleteRoleDefinitionAsync(
        Guid roleDefinitionId, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets or clears the IsManagement flag on a role definition.
    /// Cannot be changed while members are assigned to the role.
    /// </summary>
    Task SetRoleIsManagementAsync(
        Guid roleDefinitionId, bool isManagement, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all role definitions for a team, with assignments and member details.
    /// </summary>
    Task<IReadOnlyList<TeamRoleDefinition>> GetRoleDefinitionsAsync(
        Guid teamId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all role definitions across active non-system teams.
    /// </summary>
    Task<IReadOnlyList<TeamRoleDefinition>> GetAllRoleDefinitionsAsync(
        CancellationToken cancellationToken = default);

    // ==========================================================================
    // Team Role Assignments
    // ==========================================================================

    /// <summary>
    /// Assigns a team member to the next available slot in a role definition.
    /// </summary>
    Task<TeamRoleAssignment> AssignToRoleAsync(
        Guid roleDefinitionId, Guid targetUserId, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a team member's assignment from a role definition.
    /// </summary>
    Task UnassignFromRoleAsync(
        Guid roleDefinitionId, Guid teamMemberId, Guid actorUserId,
        CancellationToken cancellationToken = default);

    // ==========================================================================
    // Cache Helpers
    // ==========================================================================

    /// <summary>
    /// Removes a user from all teams in the cache (e.g., on account deletion/suspension).
    /// </summary>
    void RemoveMemberFromAllTeamsCache(Guid userId);
}
