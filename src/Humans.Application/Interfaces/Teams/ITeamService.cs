using Humans.Application.Architecture;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using NodaTime;

namespace Humans.Application.Interfaces.Teams;

/// <summary>
/// Cached team read-model used by <c>CachingTeamService</c> to project
/// every Teams-section read entirely from memory.
/// </summary>
/// <remarks>
/// <para>Cache size estimate (T-01, 500-user scale):</para>
/// <list type="bullet">
/// <item><description>~50 teams × ~30 fields ≈ ~3 KB per record, plus ~10 members × ~250 B ≈ ~2.5 KB.</description></item>
/// <item><description><c>RoleDefinitions</c> adds ~5 defs × ~200 B + their assignments (~5 × ~80 B) ≈ ~1.4 KB per team.</description></item>
/// <item><description><c>PageContent</c> is the largest variable field — markdown, capped in practice at a few KB; budget ~5 KB per team.</description></item>
/// <item><description>Full population footprint: ~50 teams × ~12 KB ≈ ~0.6 MB. Well under the 50 MB per-projection budget.</description></item>
/// </list>
/// </remarks>
public record TeamInfo(
    Guid Id, string Name, string? Description, string Slug,
    bool IsActive, bool IsSystemTeam, SystemTeamType SystemTeamType, bool RequiresApproval,
    bool IsPublicPage, bool IsHidden, bool IsPromotedToDirectory, Instant CreatedAt, List<TeamMemberInfo> Members,
    Guid? ParentTeamId = null,
    string? GoogleGroupPrefix = null,
    bool HasBudget = false,
    bool IsSensitive = false,
    Instant? UpdatedAt = null,
    string? CustomSlug = null,
    IReadOnlySet<Guid>? ManagementRoleHolderUserIds = null,
    IReadOnlyList<TeamRoleDefinitionSnapshot>? RoleDefinitions = null,
    IReadOnlyList<Guid>? ChildTeamIds = null,
    bool ShowCoordinatorsOnPublicPage = true,
    string? PageContent = null,
    IReadOnlyList<CallToAction>? CallsToAction = null,
    Instant? PageContentUpdatedAt = null,
    Guid? PageContentUpdatedByUserId = null,
    int PendingRequestCount = 0)
{
    /// <summary>
    /// Full Google Group email address, or null if no prefix is set. Mirrors
    /// the canonical formula on <see cref="Team.GoogleGroupEmail"/> so callers
    /// stitching via the cache get the same value without touching the entity.
    /// </summary>
    public string? GoogleGroupEmail =>
        GoogleGroupPrefix is null
            ? null
            : $"{GoogleGroupPrefix}@{DomainConstants.GoogleGroupDomain}";
}

public record TeamMemberInfo(
    Guid TeamMemberId, Guid UserId, string DisplayName,
    string? Email, string? ProfilePictureUrl, TeamMemberRole Role, Instant JoinedAt,
    GoogleEmailStatus GoogleEmailStatus = GoogleEmailStatus.Unknown);

public record TeamDirectorySummary(
    Guid Id,
    string Name,
    string? Description,
    string Slug,
    int MemberCount,
    bool IsSystemTeam,
    bool IsHidden,
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
    IReadOnlyList<TeamDirectorySummary> SystemTeams,
    IReadOnlyList<TeamDirectorySummary> HiddenTeams);

public record TeamDetailMemberSummary(
    Guid UserId,
    string DisplayName,
    string? Email,
    string? ProfilePictureUrl,
    TeamMemberRole Role,
    Instant JoinedAt);

public record TeamDetailResult(
    TeamPageTeamSummary Team,
    IReadOnlyList<TeamDetailMemberSummary> Members,
    IReadOnlyList<TeamPageTeamLink> ChildTeams,
    IReadOnlyList<TeamRoleDefinitionSnapshot> RoleDefinitions,
    bool IsAuthenticated,
    bool IsCurrentUserMember,
    bool IsCurrentUserCoordinator,
    bool CanCurrentUserJoin,
    bool CanCurrentUserLeave,
    bool CanCurrentUserManage,
    bool CanCurrentUserEditTeam,
    Guid? CurrentUserPendingRequestId,
    int PendingRequestCount);

public sealed record TeamPageCallToActionInput(string? Text, string? Url, CallToActionStyle Style);

public sealed record TeamPageUpdateResult(bool Succeeded, string? ErrorMessage)
{
    public static TeamPageUpdateResult Success() => new(true, null);

    public static TeamPageUpdateResult Failed(string message) => new(false, message);
}

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

public record TeamRoleReconciliationMembership(
    Guid TeamMemberId,
    Guid UserId,
    Guid TeamId,
    string TeamName,
    TeamMemberRole Role,
    SystemTeamType SystemTeamType,
    bool HasManagementRoleAssignment);

public record TeamJoinRequestSnapshot(
    Guid Id,
    Guid TeamId,
    string? TeamName,
    Guid UserId,
    string? UserDisplayName,
    string? UserEmail,
    string? UserProfilePictureUrl,
    TeamJoinRequestStatus Status,
    string? Message,
    Instant RequestedAt,
    Instant? ResolvedAt,
    string? ReviewNotes);

public record SystemTeamMembershipSnapshot(
    Guid Id,
    string Name,
    string Slug,
    bool IsHidden,
    SystemTeamType SystemTeamType,
    IReadOnlyList<Guid> ActiveMemberUserIds);

public record TeamActiveMemberSnapshot(
    Guid TeamId,
    Guid TeamMemberId,
    Guid UserId,
    string DisplayName,
    string? Email,
    string? ProfilePictureUrl,
    GoogleEmailStatus GoogleEmailStatus,
    TeamMemberRole Role,
    Instant JoinedAt);

/// <summary>
/// Service for managing teams and team membership.
/// </summary>
/// <remarks>
/// Surface-budget recent history (newest first):
/// <list type="bullet">
///   <item>51→48 — ITeamServiceRead split: GetTeamsAsync/GetTeamAsync/SearchAsync/GetTeamBySlugAsync(TeamInfo) onto ITeamServiceRead; entity slug method renamed GetTeamEntityBySlugAsync.</item>
///   <item>54→51 — ITeamServiceRead split prep: removed GetPendingRequestCountsByTeamIdsAsync; made CanUserApproveRequestsForTeamAsync and GetAllRoleDefinitionsAsync private.</item>
///   <item>54→54 — added TeamInfo.ManagementRoleHolderUserIds + RoleDefinitions; drained 6 readers off DB onto team cache.</item>
/// </list>
/// </remarks>
[SurfaceBudget(48)]
public interface ITeamService : ITeamServiceRead, IApplicationService
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
    /// Gets a team entity by its slug (Teams-internal use; external sections
    /// should use <see cref="ITeamServiceRead.GetTeamBySlugAsync"/> for the
    /// <see cref="TeamInfo"/> projection).
    /// </summary>
    Task<Team?> GetTeamEntityBySlugAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a team by its ID.
    /// </summary>
    Task<Team?> GetTeamByIdAsync(Guid teamId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all active teams.
    /// </summary>
    Task<IReadOnlyList<Team>> GetAllTeamsAsync(CancellationToken cancellationToken = default);

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
    /// Gets pending join requests for a specific team.
    /// </summary>
    Task<IReadOnlyList<TeamJoinRequestSnapshot>> GetPendingRequestsForTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a user's pending request for a team, if any.
    /// </summary>
    Task<TeamJoinRequestSnapshot?> GetUserPendingRequestAsync(
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
    /// Gets the management role definition name for each team that has one.
    /// </summary>
    /// <param name="teamIds">The team IDs to check.</param>
    /// <returns>Dictionary mapping team ID to the management role name.</returns>
    Task<IReadOnlyDictionary<Guid, string>> GetManagementRoleNamesByTeamIdsAsync(
        IEnumerable<Guid> teamIds,
        CancellationToken cancellationToken = default);

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
    Task<TeamPageUpdateResult> UpdateTeamPageContentAsync(
        Guid teamId,
        string? pageContent,
        IReadOnlyList<TeamPageCallToActionInput> callsToAction,
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
        bool canToggleManagement = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a role definition and its assignments.
    /// </summary>
    Task DeleteRoleDefinitionAsync(
        Guid roleDefinitionId, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Toggles the IsManagement flag on a role definition.
    /// Cannot be enabled while members are assigned to the role.
    /// </summary>
    Task<TeamRoleManagementToggleResult> ToggleRoleIsManagementAsync(
        Guid roleDefinitionId, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all role definitions for a team, with assignments and member details.
    /// </summary>
    Task<IReadOnlyList<TeamRoleDefinitionSnapshot>> GetRoleDefinitionsAsync(
        Guid teamId, CancellationToken cancellationToken = default);

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
    /// Returns the active (non-Volunteers) teams the user belongs to with the
    /// user's role on each team. Callers that only need names project via
    /// <c>.Select(m =&gt; m.TeamName)</c>. Display ordering is the caller's
    /// responsibility (rendering layer).
    /// </summary>
    Task<IReadOnlyList<Models.TeamMembership>> GetActiveTeamMembershipsForUserAsync(
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
    /// Adds a team member with an explicit <paramref name="role"/> and <paramref name="joinedAt"/>
    /// timestamp, without emitting audit entries, outbox events, or user-facing emails. This is
    /// a restricted seed/migration-only path; production membership changes must go through
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
    /// Permanently deletes a team and its Teams-owned child rows. Requires the
    /// current authenticated user to hold the full Admin role. The team must
    /// have no linked Google resources — <c>GoogleResource → Team</c> is
    /// configured with <c>OnDelete(Restrict)</c>, so the caller must unlink
    /// resources via <see cref="ITeamResourceService"/> first.
    /// </summary>
    Task<bool> PermanentlyDeleteTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default);

    // ==========================================================================
    // Budget Integration
    // ==========================================================================

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

    /// <summary>
    /// Returns the total number of pending team join requests across all teams.
    /// Used by the notification meter to surface pending requests to Admin
    /// without letting the Notifications section read <c>team_join_requests</c>
    /// directly (design-rules §2c).
    /// </summary>
    Task<int> GetTotalPendingJoinRequestCountAsync(CancellationToken cancellationToken = default);

    // ==========================================================================
    // System team sync support (issue #570 — §15 Google-writing jobs)
    //
    // Narrow read/write methods used exclusively by SystemTeamSyncJob so the
    // job can drop its HumansDbContext dependency. Each mutation commits in
    // its own repository-owned unit of work; the caller fan-outs Google sync
    // calls externally.
    // ==========================================================================

    /// <summary>
    /// Loads every active membership with enough role-assignment / role-
    /// definition / team context to decide coordinator promotion and
    /// demotion in-memory. Used by <c>SystemTeamSyncJob.ReconcileCoordinatorRolesAsync</c>.
    /// </summary>
    Task<IReadOnlyList<TeamRoleReconciliationMembership>> GetActiveMembershipsForRoleReconciliationAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a bulk list of <see cref="TeamMember.Role"/> changes (promote
    /// to Coordinator / demote to Member) in a single save. Invalidates the
    /// ActiveTeams cache if any change is applied. Returns the number of
    /// memberships updated.
    /// </summary>
    Task<int> ApplyMemberRoleChangesAsync(
        IReadOnlyCollection<(Guid TeamMemberId, TeamMemberRole Role)> changes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a system-team membership reconciliation in a single save:
    /// inserts new <see cref="TeamMember"/> rows for <paramref name="userIdsToAdd"/>
    /// with <see cref="TeamMemberRole.Member"/> + <c>JoinedAt=now</c>, and
    /// soft-removes the active memberships for <paramref name="userIdsToRemove"/>
    /// by stamping <see cref="TeamMember.LeftAt"/> and cascade-deleting any
    /// attached <see cref="TeamRoleAssignment"/> rows. Bumps
    /// <see cref="Team.UpdatedAt"/> and invalidates the ActiveTeams cache
    /// when at least one change lands. Returns true when any writes occur.
    /// </summary>
    /// <remarks>
    /// The caller is expected to fan out Google-sync calls
    /// (<c>AddUserToTeamResourcesAsync</c> / <c>RemoveUserFromTeamResourcesAsync</c>)
    /// and audit entries per user after this method returns.
    /// </remarks>
    Task<bool> ApplySystemTeamMembershipDeltaAsync(
        Guid teamId,
        IReadOnlyCollection<Guid> userIdsToAdd,
        IReadOnlyCollection<Guid> userIdsToRemove,
        Instant now,
        CancellationToken cancellationToken = default);
}

public sealed record TeamRoleDefinitionSnapshot(
    Guid Id,
    Guid TeamId,
    string TeamName,
    string TeamSlug,
    string Name,
    string? Description,
    int SlotCount,
    IReadOnlyList<SlotPriority> Priorities,
    int SortOrder,
    bool IsManagement,
    RolePeriod Period,
    bool IsPublic,
    IReadOnlyList<TeamRoleAssignmentSnapshot> Assignments);

public sealed record TeamRoleManagementToggleResult(
    string RoleName,
    bool IsManagement);

public sealed record TeamRoleAssignmentSnapshot(
    Guid Id,
    Guid TeamMemberId,
    int SlotIndex,
    Guid? AssignedUserId);
