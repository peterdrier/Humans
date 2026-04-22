using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using NodaTime;

namespace Humans.Application.Interfaces;

public record CachedTeam(
    Guid Id, string Name, string? Description, string Slug,
    bool IsSystemTeam, SystemTeamType SystemTeamType, bool RequiresApproval,
    bool IsPublicPage, bool IsHidden, bool IsPromotedToDirectory, Instant CreatedAt, List<CachedTeamMember> Members,
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
/// Flat projection of an active coordinator row — the user who holds the
/// Coordinator membership role on a specific team. Used by cross-section
/// callers (shift dashboard) that need per-team coordinator lists without
/// joining across the Teams-owned tables themselves.
/// </summary>
public record TeamCoordinatorRef(Guid TeamId, Guid UserId);

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
    /// Returns the display name of the team whose <c>GoogleGroupPrefix</c> matches
    /// <paramref name="googleGroupPrefix"/> (case-insensitive), or null if no team
    /// uses that prefix. Used by @nobodies.team provisioning to block personal
    /// prefixes that would collide with a team group on the same domain.
    /// </summary>
    Task<string?> GetTeamNameByGoogleGroupPrefixAsync(
        string googleGroupPrefix,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a set of team IDs to their display names WITHOUT filtering by
    /// <see cref="Team.IsActive"/>. Used by the GDPR export, where historical
    /// records (shift signups, audit entries) may reference teams that have
    /// since been deactivated — those users still deserve a name in their
    /// downloaded data, not <c>null</c>. The returned dictionary contains one
    /// entry per input ID that resolves; unknown IDs are simply absent.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetTeamNamesByIdsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken = default);

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
        bool? isPromotedToDirectory = null,
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
    /// Sets <c>Team.GoogleGroupPrefix</c> to <paramref name="prefix"/> (may be
    /// null to clear) and persists the change. Returns the previous prefix so
    /// callers can revert on downstream-service failure. Returns (<c>false</c>,
    /// <c>null</c>) if the team does not exist. Narrow alternative to
    /// <see cref="UpdateTeamAsync"/> for flows that only need to touch the
    /// Google-group wiring.
    /// </summary>
    Task<(bool Updated, string? PreviousPrefix)> SetGoogleGroupPrefixAsync(
        Guid teamId, string? prefix, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Sets the <see cref="TeamMember.Role"/> for an active membership to the
    /// given value. Used by <c>DuplicateAccountService.ResolveAsync</c> to
    /// preserve a coordinator role when migrating the membership from the
    /// archived source account to the target. No-op if the user has no active
    /// membership on the team.
    /// </summary>
    Task SetMemberRoleAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
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
    // Coordinator Queries
    // ==========================================================================

    /// <summary>
    /// Gets all non-system team IDs where the user is a coordinator or has a management role.
    /// Used by shift services for authorization — avoids cross-service team table queries.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUserCoordinatedTeamIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the user IDs of all active coordinators for a team (Coordinator member role).
    /// Used by shift services for notification dispatch.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetCoordinatorUserIdsAsync(
        Guid teamId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the names of all active (non-Volunteers) teams the user belongs to,
    /// ordered alphabetically. Used for profile popover display.
    /// </summary>
    Task<IReadOnlyList<string>> GetActiveTeamNamesForUserAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues AddUserToTeamResources sync events for all active team
    /// memberships of a user. Used when the user's Google service email changes.
    /// </summary>
    Task EnqueueGoogleResyncForUserTeamsAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the Team rows for the requested IDs <b>and</b> any referenced parent
    /// teams, so the caller can resolve the "department" (parent or self) for each
    /// team via dictionary lookups without navigating <c>team.ParentTeam</c>. Used
    /// by the shift coordinator dashboard to stitch department rows in memory after
    /// moving off a cross-domain <c>.Include(Rota).ThenInclude(Team).ThenInclude(ParentTeam)</c>
    /// chain. Returned teams are not active-filtered — shifts/rotas may still
    /// reference deactivated teams and the caller still needs the name.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Team>> GetByIdsWithParentsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns one row per active (<see cref="TeamMember.LeftAt"/> is null) coordinator
    /// across the requested teams. Used by the shift coordinator dashboard to look up
    /// coordinators for teams with pending signups without reading the TeamMembers table.
    /// </summary>
    Task<IReadOnlyList<TeamCoordinatorRef>> GetActiveCoordinatorsForTeamsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the user IDs of every active (<see cref="TeamMember.LeftAt"/>
    /// is null) member of the given team. Used by cross-section callers
    /// (Tickets dashboard, coverage reporting) that need a set of member ids
    /// without loading full <see cref="TeamMember"/> entities.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveMemberUserIdsAsync(
        Guid teamId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the active non-system team names for each of the given user ids.
    /// Entries are absent for users with no active memberships.
    /// Used by the Tickets admin "who hasn't bought" view and similar
    /// cross-section admin lists.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetActiveNonSystemTeamNamesByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a team member with an explicit <paramref name="role"/> and <paramref name="joinedAt"/>
    /// timestamp, without emitting audit entries, outbox events, or user-facing emails. This is
    /// a narrow seed/migration-only path; production membership changes must go through
    /// <see cref="AddMemberToTeamAsync"/>, <see cref="ApproveJoinRequestAsync"/>, or the role
    /// assignment APIs. Throws if the user is already an active member of the team.
    /// </summary>
    Task<TeamMember> AddSeededMemberAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Instant joinedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard-deletes every team whose <see cref="Team.Name"/> ends with
    /// <paramref name="nameSuffix"/>, along with its <see cref="TeamMember"/> rows and
    /// <see cref="TeamJoinRequest"/> rows. Narrow seed/test-only cleanup path that
    /// bypasses <see cref="DeleteTeamAsync"/>'s soft-delete semantics so a subsequent
    /// reseed can reuse the same slugs without collisions. Returns the number of teams
    /// deleted.
    /// </summary>
    Task<int> HardDeleteSeededTeamsAsync(
        string nameSuffix,
        CancellationToken cancellationToken = default);

    // ==========================================================================
    // Budget Integration
    // ==========================================================================

    /// <summary>
    /// Returns active teams flagged with <c>HasBudget</c>, ordered by name.
    /// Used by the Budget section to seed department categories when a budget
    /// year is created or synced. Returns just <see cref="TeamOptionDto"/> so
    /// the Budget section does not navigate the Teams graph.
    /// </summary>
    Task<IReadOnlyList<TeamOptionDto>> GetBudgetableTeamsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the department-scoped team IDs a user can coordinate for budget
    /// purposes: departments (<c>ParentTeamId is null</c>) where the user is a
    /// direct coordinator or holds a management role assignment, plus every
    /// child team of those departments. Encapsulates the "department coordinators
    /// manage child team budgets" policy inside the Teams section so the Budget
    /// service does not read team graph tables itself.
    /// </summary>
    Task<IReadOnlyCollection<Guid>> GetEffectiveBudgetCoordinatorTeamIdsAsync(
        Guid userId, CancellationToken cancellationToken = default);

    // ==========================================================================
    // Cache Helpers
    // ==========================================================================

    /// <summary>
    /// Removes a user from all teams in the cache (e.g., on account deletion/suspension).
    /// </summary>
    void RemoveMemberFromAllTeamsCache(Guid userId);

    /// <summary>
    /// Evicts the ActiveTeams master cache entry so the next read repopulates
    /// from the database. Use when an orchestrator can't rely on the in-place
    /// cache mutations the team service performs during writes — typically
    /// after a transactional rollback, where the DB has reverted but the
    /// in-memory mutations haven't.
    /// </summary>
    void InvalidateActiveTeamsCache();

    /// <summary>
    /// Ends all active team memberships for a user, removes their team role assignments,
    /// and returns the count of ended memberships. Used during account deletion.
    /// </summary>
    Task<int> RevokeAllMembershipsAsync(Guid userId, CancellationToken cancellationToken = default);
}
