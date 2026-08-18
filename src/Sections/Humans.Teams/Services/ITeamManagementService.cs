using Humans.Teams.Contracts;
using Humans.Teams.Domain;
using NodaTime;

namespace Humans.Teams.Services;

internal sealed record TeamDirectorySummary(
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

internal sealed record TeamDirectoryResult(
    bool IsAuthenticated,
    bool CanCreateTeam,
    IReadOnlyList<TeamDirectorySummary> MyTeams,
    IReadOnlyList<TeamDirectorySummary> Departments,
    IReadOnlyList<TeamDirectorySummary> SystemTeams,
    IReadOnlyList<TeamDirectorySummary> HiddenTeams);

internal sealed record TeamDetailMemberSummary(
    Guid UserId,
    string DisplayName,
    string? Email,
    string? ProfilePictureUrl,
    TeamMemberRole Role,
    Instant JoinedAt);

internal sealed record TeamDetailResult(
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

internal sealed record TeamPageCallToActionInput(string? Text, string? Url, CallToActionStyle Style);

internal sealed record TeamPageUpdateResult(bool Succeeded, string? ErrorMessage)
{
    public static TeamPageUpdateResult Success() => new(true, null);

    public static TeamPageUpdateResult Failed(string message) => new(false, message);
}

internal sealed record MyTeamMembershipSummary(
    Guid TeamId,
    string TeamName,
    string TeamSlug,
    bool IsSystemTeam,
    TeamMemberRole Role,
    Instant JoinedAt,
    bool CanLeave,
    int PendingRequestCount);

internal sealed record TeamRosterSlotSummary(
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

internal sealed record AdminTeamSummary(
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

internal sealed record AdminTeamListResult(
    IReadOnlyList<AdminTeamSummary> Teams,
    int TotalCount);

/// <summary>
/// Flat projection of an active coordinator row — the user who holds the
/// Coordinator membership role on a specific team. Used by cross-section
/// callers (shift dashboard) that need per-team coordinator lists without
/// joining across the Teams-owned tables themselves.
/// </summary>
internal sealed record TeamJoinRequestSnapshot(
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

/// <summary>
/// Persistence-free read model for a team early-entry grant, returned by the
/// Teams-internal management read so the service boundary does not expose the
/// <see cref="TeamEarlyEntryGrant"/> entity. The human display name is resolved
/// by the caller via <c>IUserServiceRead</c> from <see cref="UserId"/>.
/// </summary>
internal sealed record TeamEarlyEntryGrantInfo(
    Guid Id,
    Guid UserId,
    LocalDate EntryDate,
    string ProjectName);

/// <summary>
/// Service for managing teams and team membership.
/// </summary>

/// <summary>
/// The Teams section's own service surface: the directory, detail, join-request, roster,
/// role-definition, role-assignment, team-page and early-entry members, plus the reads that
/// return the section's entities. Internal because nothing outside the assembly calls any of
/// it — the cross-section half is <see cref="ITeamService"/>, which this inherits so the
/// section's controllers and its caching decorator keep one injection
/// (design §15 step 5b, Notifications' shape).
/// </summary>
/// <remarks>
/// The interface survives internalisation for the reason step 5 records: the caching decorator
/// needs the seam, and MA0053 seals the concrete <c>TeamService</c> so NSubstitute cannot
/// stand in for it.
/// </remarks>
internal interface ITeamManagementService : ITeamService
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
        bool? earlyEntryEnabled = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a team and — when a Google group prefix is set — provisions its
    /// Google Group, owning the compensation: if provisioning fails the prefix
    /// is cleared so the team never points at a group that does not exist.
    /// <c>GroupWarning</c> carries the operator-facing message in that case.
    /// </summary>
    Task<TeamWithGroupResult> CreateTeamWithGoogleGroupAsync(
        string name,
        string? description,
        bool requiresApproval,
        Guid? parentTeamId = null,
        string? googleGroupPrefix = null,
        bool isHidden = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a team's details, then reconciles its Google Group link.
    /// <c>GroupWarning</c> carries the operator-facing message when the group
    /// sync failed or needs reactivation confirmation; the team update itself
    /// has already succeeded in that case.
    /// </summary>
    Task<TeamWithGroupResult> UpdateTeamWithGoogleGroupAsync(
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
        bool? earlyEntryEnabled = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes (deactivates) a team.
    /// </summary>
    Task DeleteTeamAsync(Guid teamId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Joins a team, dispatching on the team's join policy: approval-required
    /// teams get a pending <c>TeamJoinRequest</c>, open teams an immediate
    /// membership. The outcome tells the caller which happened.
    /// </summary>
    Task<TeamJoinOutcome> JoinTeamAsync(
        Guid teamId,
        Guid userId,
        string? message,
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
    /// Removes a member from a team (admin action). When the removed member was a
    /// coordinator, reconciles their Coordinators system-team membership as part of
    /// the mutation.
    /// </summary>
    Task RemoveMemberAsync(
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
        int? estimatedHours = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing role definition.
    /// </summary>
    Task<TeamRoleDefinition> UpdateRoleDefinitionAsync(
        Guid roleDefinitionId, string name, string? description, int slotCount,
        List<SlotPriority> priorities, int sortOrder, bool isManagement, RolePeriod period, Guid actorUserId,
        bool isPublic = true,
        bool canToggleManagement = true,
        int? estimatedHours = null,
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
    /// Assigns a team member to the next available slot in a role definition,
    /// then reconciles the target's Coordinators system-team membership
    /// (management roles promote).
    /// </summary>
    Task<TeamRoleAssignment> AssignToRoleAsync(
        Guid roleDefinitionId, Guid targetUserId, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a team member's assignment from a role definition, then
    /// reconciles the target's Coordinators system-team membership.
    /// </summary>
    Task UnassignFromRoleAsync(
        Guid roleDefinitionId, Guid teamMemberId, Guid actorUserId,
        CancellationToken cancellationToken = default);

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
    /// Returns the total number of pending team join requests across all teams.
    /// Used by the notification meter to surface pending requests to Admin
    /// without letting the Notifications section read <c>team_join_requests</c>
    /// directly (design-rules §2c).
    /// </summary>
    Task<int> GetTotalPendingJoinRequestCountAsync(CancellationToken cancellationToken = default);

    // ==========================================================================
    // Early-Entry Grants (Teams-internal; called by TeamAdminController for the
    // per-team management page, plus AccountDeletionService for erasure and
    // IUserMerge for merge). Not exposed on ITeamServiceRead — cross-section reads
    // go through IEarlyEntryProvider.
    // ==========================================================================

    /// <summary>
    /// Gets the early-entry grants for a single team (management view) as a
    /// persistence-free read model. Display ordering is the caller's
    /// responsibility (rendering layer).
    /// </summary>
    Task<IReadOnlyList<TeamEarlyEntryGrantInfo>> GetEarlyEntryGrantsForTeamAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Grants early entry to a user for a team. Throws if the team does not have
    /// <see cref="Team.EarlyEntryEnabled"/> set. Records an audit entry and evicts
    /// the user's EE cache.
    /// </summary>
    Task AddEarlyEntryGrantAsync(Guid teamId, Guid userId, LocalDate entryDate, string projectName, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Updates the entry date and project label of an existing grant. The grant
    /// must belong to <paramref name="teamId"/>; a grant on another team is
    /// treated the same as not found.
    /// </summary>
    Task EditEarlyEntryGrantAsync(Guid teamId, Guid grantId, LocalDate entryDate, string projectName, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Revokes an early-entry grant. Idempotent (no-op if already gone). The
    /// grant must belong to <paramref name="teamId"/>; a grant on another team
    /// is treated the same as not found (no-op).
    /// </summary>
    Task RemoveEarlyEntryGrantAsync(Guid teamId, Guid grantId, Guid actorUserId, CancellationToken ct = default);
}

internal sealed record TeamRoleManagementToggleResult(
    string RoleName,
    bool IsManagement);

/// <summary>
/// A team create/update plus the outcome of its Google Group reconciliation.
/// <see cref="GroupWarning"/> is null when the group is in order; otherwise the
/// operator-facing message to surface alongside the success flash.
/// </summary>
internal sealed record TeamWithGroupResult(Team Team, string? GroupWarning);

/// <summary>What <see cref="ITeamService.JoinTeamAsync"/> did, per the team's join policy.</summary>
internal enum TeamJoinOutcome
{
    /// <summary>Open team — the user is now an active member.</summary>
    Joined,

    /// <summary>Approval-required team — a pending join request was created.</summary>
    RequestSubmitted,
}
