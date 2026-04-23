using Humans.Application.Interfaces.Teams;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Teams section's tables: <c>teams</c>,
/// <c>team_members</c>, <c>team_join_requests</c>,
/// <c>team_join_request_state_histories</c>, <c>team_role_definitions</c>,
/// <c>team_role_assignments</c>. The only non-test file that touches these
/// DbSets after the Teams migration lands (§15 Part 1, issue #540).
///
/// <para>
/// Aggregate-local navs (<c>Team.ParentTeam</c>, <c>Team.ChildTeams</c>,
/// <c>Team.Members</c>, <c>Team.JoinRequests</c>, <c>Team.RoleDefinitions</c>,
/// <c>TeamJoinRequest.StateHistory</c>, <c>TeamMember.Team</c>,
/// <c>TeamRoleDefinition.Team</c>, <c>TeamRoleAssignment.TeamRoleDefinition</c>,
/// <c>TeamRoleAssignment.TeamMember</c>) are <c>.Include</c>-d here where
/// needed. Cross-domain navs (<c>TeamMember.User</c>, <c>TeamJoinRequest.User</c>,
/// etc.) are never navigated — callers stitch display data from
/// <see cref="Users.IUserService"/>. See design-rules §6.
/// </para>
/// </summary>
public interface ITeamRepository
{
    // ==========================================================================
    // Team reads
    // ==========================================================================

    /// <summary>Load a team by id (no navigation). Detached.</summary>
    Task<Team?> GetByIdAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Load a team by id with its active members, parent team, and child teams
    /// eagerly. Detached. Used by the team detail page.
    /// </summary>
    Task<Team?> GetByIdWithRelationsAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Load a team for mutation (tracked, no navigation). Used by writes
    /// that will mutate scalar properties and call <see cref="UpdateTeamAsync"/>
    /// on the same repository/method.
    /// </summary>
    Task<Team?> FindForMutationAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Load a team by its slug or custom slug with active members, parent,
    /// and children. Detached. Used by the public team detail page.
    /// </summary>
    Task<Team?> GetBySlugWithRelationsAsync(string normalizedSlug, CancellationToken ct = default);

    /// <summary>
    /// Load every team id whose <c>Slug</c> OR <c>CustomSlug</c> matches one
    /// of the given candidates, optionally excluding a specific team id.
    /// Used by create/update to detect slug collisions.
    /// </summary>
    Task<bool> SlugExistsAsync(string slug, Guid? excludingTeamId, CancellationToken ct = default);

    /// <summary>All active teams ordered by name, with active members and children.</summary>
    Task<IReadOnlyList<Team>> GetAllActiveAsync(CancellationToken ct = default);

    /// <summary>
    /// All active teams with active members eagerly loaded (the shape consumed
    /// by the in-service cache snapshot). Detached.
    /// </summary>
    Task<IReadOnlyList<Team>> GetAllActiveWithMembersAsync(CancellationToken ct = default);

    /// <summary>All active non-system teams ordered by name, with active members.</summary>
    Task<IReadOnlyList<Team>> GetAllActiveUserCreatedAsync(CancellationToken ct = default);

    /// <summary>Active teams projected to id/name for dropdowns.</summary>
    Task<IReadOnlyList<TeamOptionDto>> GetActiveOptionsAsync(CancellationToken ct = default);

    /// <summary>Active teams with <c>HasBudget=true</c> projected to id/name.</summary>
    Task<IReadOnlyList<TeamOptionDto>> GetBudgetableOptionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Resolve a set of team ids to their display names, without any
    /// <c>IsActive</c> filter (historical references in GDPR exports etc).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetNamesByIdsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default);

    /// <summary>
    /// Return the team display name whose <c>GoogleGroupPrefix</c> matches
    /// <paramref name="prefix"/> (case-insensitive, wildcard-safe). Returns
    /// null if no team uses that prefix.
    /// </summary>
    Task<string?> GetNameByGoogleGroupPrefixAsync(string prefix, CancellationToken ct = default);

    /// <summary>
    /// Paged list of teams (all active/inactive) with members, pending join
    /// requests, and role definitions eagerly loaded for the admin table.
    /// </summary>
    Task<(IReadOnlyList<Team> Items, int TotalCount)> GetAllForAdminAsync(
        int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Load the requested team ids plus any referenced parent teams that
    /// aren't already in the requested set. Used by the shift coordinator
    /// dashboard for department-stitching without cross-domain
    /// <c>.Include(Rota).ThenInclude(Team).ThenInclude(ParentTeam)</c>.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Team>> GetByIdsWithParentsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default);

    /// <summary>
    /// Is <paramref name="teamId"/> an active team that has at least one
    /// active child team? Used by <c>UpdateTeamAsync</c> / <c>DeleteTeamAsync</c>.
    /// </summary>
    Task<bool> HasActiveChildrenAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Load active child teams of <paramref name="parentTeamIds"/> with their
    /// id + parent id only. Used by <c>GetMyTeamMembershipsAsync</c> to
    /// aggregate pending counts for coordinator departments.
    /// </summary>
    Task<IReadOnlyList<(Guid ChildId, Guid ParentId)>> GetActiveChildIdsByParentsAsync(
        IReadOnlyCollection<Guid> parentTeamIds, CancellationToken ct = default);

    /// <summary>Adds a new team and persists. The team is returned tracked-free.</summary>
    Task AddTeamAsync(Team team, CancellationToken ct = default);

    /// <summary>
    /// Persists a <see cref="Team"/> that was loaded via
    /// <see cref="FindForMutationAsync"/> and mutated in the service layer.
    /// Commits immediately.
    /// </summary>
    Task UpdateTeamAsync(Team team, CancellationToken ct = default);

    /// <summary>
    /// Creates a team and, in the same transaction, forces its
    /// <c>RequiresApproval</c> column to <paramref name="requiresApproval"/>
    /// after insert (works around the EF store-default sentinel).
    /// Returns <c>true</c> on success, or <c>false</c> when persistence
    /// aborted because of a unique-constraint collision (typically a slug
    /// race against a concurrent create). The service layer uses the
    /// <c>false</c> result to drive its suffix-retry loop without needing
    /// to catch EF Core exception types directly.
    /// </summary>
    Task<bool> AddTeamWithRequiresApprovalOverrideAsync(
        Team team, bool requiresApproval, CancellationToken ct = default);

    /// <summary>
    /// In a single transaction: close every active <see cref="TeamMember"/>
    /// for the team, mark the team <c>IsActive=false</c> with the given
    /// timestamp, and commit. Returns the count of memberships closed.
    /// </summary>
    Task<int> DeactivateTeamAsync(Guid teamId, Instant now, CancellationToken ct = default);

    /// <summary>
    /// In a single transaction: set <c>GoogleGroupPrefix</c> on the team and
    /// commit. Returns the previous value and whether the row was found.
    /// </summary>
    Task<(bool Updated, string? PreviousPrefix)> SetGoogleGroupPrefixAsync(
        Guid teamId, string? prefix, CancellationToken ct = default);

    // ==========================================================================
    // TeamMember reads
    // ==========================================================================

    /// <summary>
    /// Get the user's active team memberships with <c>Team</c> and
    /// <c>Team.ParentTeam</c> eagerly loaded. Detached.
    /// </summary>
    Task<IReadOnlyList<TeamMember>> GetActiveByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Get active members of a team with aggregate-local data, detached.
    /// Cross-domain User data is not included — caller stitches via
    /// <see cref="Users.IUserService"/>.
    /// </summary>
    Task<IReadOnlyList<TeamMember>> GetActiveMembersAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Get the active UserIds of every member of a team.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveMemberUserIdsAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Get active user ids with the <see cref="TeamMemberRole.Coordinator"/>
    /// role on the given team.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetCoordinatorUserIdsAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// One row per active coordinator across the requested teams.
    /// </summary>
    Task<IReadOnlyList<TeamCoordinatorRef>> GetActiveCoordinatorsForTeamsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default);

    /// <summary>
    /// Active non-system team ids where the user holds
    /// <see cref="TeamMemberRole.Coordinator"/>.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUserCoordinatorTeamIdsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Team ids where the user is a direct department coordinator (parent is
    /// null and <see cref="TeamMemberRole.Coordinator"/>).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUserDepartmentCoordinatorTeamIdsAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Check whether the user has an active membership on the team.
    /// </summary>
    Task<bool> IsActiveMemberAsync(Guid teamId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Get the single tracked active membership for a user on a team.
    /// Used by writes (leave, remove, set-role) that will mutate it.
    /// </summary>
    Task<TeamMember?> FindActiveMemberForMutationAsync(
        Guid teamId, Guid userId, CancellationToken ct = default);

    /// <summary>Inserts a new team member in its own transaction.</summary>
    Task AddMemberAsync(TeamMember member, CancellationToken ct = default);

    /// <summary>
    /// Adds a member plus a single Google-sync outbox event in a single
    /// transaction. Returns false if a unique-constraint violation indicates
    /// the user is already an active member.
    /// </summary>
    Task<bool> AddMemberWithOutboxAsync(
        TeamMember member, GoogleSyncOutboxEvent outboxEvent, CancellationToken ct = default);

    /// <summary>
    /// Approves a join request, creates a new active TeamMember, and adds the
    /// Google-sync outbox event in a single transaction. Returns false if a
    /// unique-constraint violation indicates the user is already an active
    /// member.
    /// </summary>
    Task<bool> ApproveRequestWithMemberAsync(
        TeamJoinRequest request,
        TeamMember member,
        GoogleSyncOutboxEvent? outboxEvent,
        CancellationToken ct = default);

    /// <summary>
    /// Leave/remove flow: remove a member's role assignments, set
    /// <c>LeftAt</c> on the membership, and optionally enqueue a
    /// Google-sync outbox event — all in a single transaction. Returns
    /// the role assignments that were removed (for shift-auth invalidation
    /// decisions in the service layer).
    /// </summary>
    Task<IReadOnlyList<TeamRoleAssignment>> MarkMemberLeftWithOutboxAsync(
        Guid teamMemberId, Instant leftAt, GoogleSyncOutboxEvent? outboxEvent, CancellationToken ct = default);

    /// <summary>
    /// Sets <see cref="TeamMember.Role"/> for the given membership (looked up
    /// by teamId + userId) and commits. No-op if the user is not an active
    /// member. Returns the new role actually persisted, or null if no-op.
    /// </summary>
    Task<TeamMemberRole?> SetMemberRoleAsync(
        Guid teamId, Guid userId, TeamMemberRole role, CancellationToken ct = default);

    /// <summary>
    /// Persists a withdrawn join request (state + resolved timestamp) in its
    /// own transaction. Returns true if a pending request was found and
    /// withdrawn; false otherwise.
    /// </summary>
    Task<bool> WithdrawRequestAsync(
        Guid requestId, Guid userId, Instant now, CancellationToken ct = default);

    /// <summary>
    /// Persists a rejected join request (state + reviewer + reason + resolved
    /// timestamp) in its own transaction. Returns false when the request
    /// doesn't exist or is no longer pending.
    /// </summary>
    Task<bool> RejectRequestAsync(
        Guid requestId, Guid reviewerUserId, string reason, Instant now, CancellationToken ct = default);

    /// <summary>
    /// Assign a team member to an available slot on a role definition.
    /// Optionally auto-adds the user to the team (with an outbox event)
    /// if <paramref name="autoAddMember"/> is non-null. Optionally promotes
    /// the member to <see cref="TeamMemberRole.Coordinator"/> when
    /// <paramref name="promoteToCoordinator"/> is true. Approves any pending
    /// join request. Returns the inserted assignment plus whether the team
    /// member was newly added.
    /// </summary>
    Task<(TeamRoleAssignment Assignment, bool AutoAddedToTeam, TeamMember Member)> AssignToRoleAsync(
        Guid roleDefinitionId,
        Guid targetUserId,
        Guid actorUserId,
        TeamMember? autoAddMember,
        GoogleSyncOutboxEvent? outboxEvent,
        bool promoteToCoordinator,
        Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Remove a role assignment. If the role is management and the member
    /// has no other management assignments, demote them from
    /// <see cref="TeamMemberRole.Coordinator"/> to
    /// <see cref="TeamMemberRole.Member"/>. Returns the removed-assignment's
    /// owning user id (for shift-auth invalidation).
    /// </summary>
    Task<(bool Demoted, Guid TargetUserId)> UnassignFromRoleAsync(
        Guid roleDefinitionId, Guid teamMemberId, CancellationToken ct = default);

    /// <summary>
    /// Persist an updated role definition, then demote any members that
    /// had become coordinators solely via this role and no longer have
    /// another management assignment. Returns the list of user ids demoted
    /// (empty when not applicable) and whether the active-teams cache should
    /// be invalidated by the caller (true when demotions occurred).
    /// </summary>
    Task<(IReadOnlyList<Guid> DemotedUserIds, bool DemotedMembers)> PersistRoleDefinitionUpdateAsync(
        TeamRoleDefinition definition,
        bool clearingIsManagement,
        CancellationToken ct = default);

    /// <summary>
    /// Persist SetRoleIsManagement flag changes. Runs the demote-coordinators
    /// branch server-side if <paramref name="clearingIsManagement"/> is true.
    /// Returns whether any members were demoted.
    /// </summary>
    Task<bool> PersistRoleIsManagementAsync(
        TeamRoleDefinition definition,
        bool clearingIsManagement,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the names of non-system active teams the user belongs to,
    /// grouped by user id. Used for the tickets admin dashboard.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetActiveNonSystemTeamNamesByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    // ==========================================================================
    // TeamJoinRequest reads
    // ==========================================================================

    /// <summary>
    /// Find the user's pending join request for the team (or null).
    /// </summary>
    Task<TeamJoinRequest?> FindUserPendingRequestAsync(
        Guid teamId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Find a single join request for mutation (tracked, detached before
    /// return by caller's choice).
    /// </summary>
    Task<TeamJoinRequest?> FindRequestForMutationAsync(Guid requestId, CancellationToken ct = default);

    /// <summary>
    /// Find a request and its associated <c>Team</c> eagerly loaded, tracked.
    /// Used by <c>ApproveJoinRequestAsync</c> which needs the team name.
    /// </summary>
    Task<TeamJoinRequest?> FindRequestWithTeamForMutationAsync(
        Guid requestId, CancellationToken ct = default);

    /// <summary>
    /// All pending join requests (across all teams) with the <c>Team</c>
    /// loaded. Detached. Cross-domain <c>User</c> nav is never included.
    /// </summary>
    Task<IReadOnlyList<TeamJoinRequest>> GetAllPendingWithTeamsAsync(CancellationToken ct = default);

    /// <summary>
    /// Pending join requests for the given teams, detached, cross-domain
    /// <c>User</c> nav excluded.
    /// </summary>
    Task<IReadOnlyList<TeamJoinRequest>> GetPendingForTeamIdsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default);

    /// <summary>
    /// Pending join requests for a single team, detached.
    /// </summary>
    Task<IReadOnlyList<TeamJoinRequest>> GetPendingForTeamAsync(
        Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Pending request counts keyed by team id. Teams not in the input are
    /// absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetPendingCountsByTeamIdsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default);

    /// <summary>Total pending join requests across all teams.</summary>
    Task<int> GetTotalPendingCountAsync(CancellationToken ct = default);

    /// <summary>Inserts a new join request.</summary>
    Task AddRequestAsync(TeamJoinRequest request, CancellationToken ct = default);

    // ==========================================================================
    // TeamRoleDefinition reads / writes
    // ==========================================================================

    /// <summary>
    /// Role definitions for a team with assignments and team-member data
    /// eagerly loaded. Detached.
    /// </summary>
    Task<IReadOnlyList<TeamRoleDefinition>> GetRoleDefinitionsAsync(
        Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Every role definition across active non-system teams, with Team,
    /// Assignments, and TeamMember data eagerly loaded. Detached.
    /// </summary>
    Task<IReadOnlyList<TeamRoleDefinition>> GetAllRoleDefinitionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Load a role definition (tracked) with Team + Assignments +
    /// Assignments.TeamMember loaded, for mutations like
    /// <c>UpdateRoleDefinitionAsync</c>. The nested <c>TeamMember</c> is
    /// required because the service reads <c>TeamMember.UserId</c> off the
    /// assignments to build the shift-authorization invalidation set when
    /// <c>IsManagement</c> flips.
    /// </summary>
    Task<TeamRoleDefinition?> FindRoleDefinitionForMutationAsync(
        Guid roleDefinitionId, CancellationToken ct = default);

    /// <summary>
    /// Load a role definition (tracked) with Team + Assignments +
    /// Assignments.TeamMember loaded for <c>SetRoleIsManagementAsync</c>.
    /// </summary>
    Task<TeamRoleDefinition?> FindRoleDefinitionWithMembersForMutationAsync(
        Guid roleDefinitionId, CancellationToken ct = default);

    /// <summary>
    /// Check whether any role definition on the team has the given
    /// lowercased name (used for uniqueness).
    /// </summary>
    Task<bool> RoleDefinitionNameExistsAsync(
        Guid teamId, string lowerName, Guid? excludingId, CancellationToken ct = default);

    /// <summary>
    /// Check whether another role definition in the same team is already
    /// marked as management.
    /// </summary>
    Task<bool> OtherRoleHasIsManagementAsync(
        Guid teamId, Guid excludingRoleDefinitionId, CancellationToken ct = default);

    /// <summary>
    /// Return the public management role display name for each team that
    /// has one. Teams without a management role are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetPublicManagementRoleNamesByTeamIdsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default);

    /// <summary>Inserts a new role definition.</summary>
    Task AddRoleDefinitionAsync(TeamRoleDefinition definition, CancellationToken ct = default);

    /// <summary>Removes an attached role definition and commits.</summary>
    Task RemoveRoleDefinitionAsync(TeamRoleDefinition definition, CancellationToken ct = default);

    // ==========================================================================
    // TeamRoleAssignment reads / writes
    // ==========================================================================

    /// <summary>
    /// Role assignments for a member, with their <c>TeamRoleDefinition</c>
    /// + <c>TeamRoleDefinition.Team</c> eagerly loaded, tracked for removal.
    /// </summary>
    Task<IReadOnlyList<TeamRoleAssignment>> FindAssignmentsForMemberForMutationAsync(
        Guid teamMemberId, CancellationToken ct = default);

    /// <summary>
    /// Role assignments for a collection of members (by id), tracked for
    /// bulk removal. Same shape as
    /// <see cref="FindAssignmentsForMemberForMutationAsync"/> but for many.
    /// </summary>
    Task<IReadOnlyList<TeamRoleAssignment>> FindAssignmentsForMembersForMutationAsync(
        IReadOnlyCollection<Guid> teamMemberIds, CancellationToken ct = default);

    /// <summary>
    /// Does the given role assignment exist pointing at a
    /// <see cref="TeamRoleDefinition"/> with <c>IsManagement=true</c>,
    /// excluding <paramref name="excludingAssignmentId"/>?
    /// </summary>
    Task<bool> MemberHasOtherManagementAssignmentAsync(
        Guid teamMemberId, Guid excludingAssignmentId, CancellationToken ct = default);

    /// <summary>
    /// Find the tracked assignment for unassignment, with TeamMember + User
    /// FK-only. (User display name is looked up through <c>IUserService</c>.)
    /// </summary>
    Task<TeamRoleAssignment?> FindAssignmentForMutationAsync(
        Guid roleDefinitionId, Guid teamMemberId, CancellationToken ct = default);

    /// <summary>
    /// Returns distinct user ids whose membership belongs to role definitions
    /// matching the predicate <c>IsManagement &amp;&amp; TeamId == teamId</c>.
    /// Used by <c>UpdateTeamAsync</c> to invalidate shift authorization when a
    /// team's parent changes.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUserIdsWithManagementOnTeamAsync(
        Guid teamId, CancellationToken ct = default);

    // ==========================================================================
    // Bulk / seed / revoke
    // ==========================================================================

    /// <summary>
    /// Ends every active team membership for a user and removes role
    /// assignments attached to those memberships. Returns the count of
    /// memberships closed. Commits immediately.
    /// </summary>
    Task<int> RevokeAllMembershipsAsync(Guid userId, Instant now, CancellationToken ct = default);

    /// <summary>
    /// Enqueue Google-sync AddUserToTeamResources events for every active
    /// team membership of <paramref name="userId"/>. Returns the count of
    /// events queued. Commits immediately.
    /// </summary>
    Task<int> EnqueueResyncEventsForUserAsync(
        Guid userId, Instant now, CancellationToken ct = default);

    /// <summary>
    /// Hard-deletes every team whose <c>Name.EndsWith</c> marker matches,
    /// plus its members and join requests. Returns the count of teams
    /// removed. Used only by test seeding.
    /// </summary>
    Task<int> HardDeleteBySuffixAsync(string nameSuffix, CancellationToken ct = default);

    /// <summary>
    /// Is the user's Google email status flagged as <see cref="GoogleEmailStatus.Rejected"/>?
    /// Used to suppress outbox events for users whose Google account is dead.
    /// The caller already owns the outbox semantics — this is a plain
    /// cross-section read routed through <see cref="Users.IUserService"/> in
    /// the application service, not here.
    /// </summary>
    // (no method on this interface — see IUserService.GetByIdAsync)

    // ==========================================================================
    // Outbox (Google sync) — accessed from Teams writes
    // ==========================================================================

    /// <summary>
    /// Add a single GoogleSyncOutboxEvent. Does NOT commit — caller must
    /// <see cref="SaveChangesAsync"/> in the same unit of work so the row
    /// lands atomically with the membership mutation that triggered it.
    /// Used by <c>JoinTeamDirectly</c>, <c>AddMember</c>, <c>ApproveJoinRequest</c>,
    /// <c>LeaveTeam</c>, <c>RemoveMember</c>, and <c>AssignToRole</c>.
    /// </summary>
    Task AddOutboxEventAsync(GoogleSyncOutboxEvent outboxEvent, CancellationToken ct = default);

    // ==========================================================================
    // GDPR export contribution
    // ==========================================================================

    /// <summary>
    /// Raw data for the GDPR export contributor: all the user's memberships
    /// (including left) with team name, role assignments, and timestamps.
    /// </summary>
    Task<IReadOnlyList<TeamMember>> GetAllMembershipsForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Raw data for the GDPR export contributor: all the user's join
    /// requests with team name and timestamps.
    /// </summary>
    Task<IReadOnlyList<TeamJoinRequest>> GetAllJoinRequestsForUserAsync(
        Guid userId, CancellationToken ct = default);
}
