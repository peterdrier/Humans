using Humans.Base.Interfaces.Repositories;
using Humans.Teams.Domain;
using NodaTime;

using Humans.Teams.Contracts;
namespace Humans.Teams.Data;

internal interface ITeamRepository : IRepository
{
    /// <summary>No navigation. Detached.</summary>
    Task<Team?> GetByIdAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>With active members, parent and child teams. Detached.</summary>
    Task<Team?> GetByIdWithRelationsAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>Tracked, no navigation: mutate scalars, then <see cref="UpdateTeamAsync"/>.</summary>
    Task<Team?> FindForMutationAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>Matches <c>Slug</c> or <c>CustomSlug</c>, ignoring <paramref name="excludingTeamId"/>.</summary>
    Task<bool> SlugExistsAsync(string slug, Guid? excludingTeamId, CancellationToken ct = default);

    /// <summary>With active members and children.</summary>
    Task<IReadOnlyList<Team>> GetAllActiveAsync(CancellationToken ct = default);

    /// <summary>Active and inactive, with active members. Detached.</summary>
    Task<IReadOnlyList<Team>> GetAllWithMembersAsync(CancellationToken ct = default);

    /// <summary>Teams with no management role holder are omitted.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> GetActiveManagementRoleHolderUserIdsByTeamAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Ordered by <c>SortOrder</c> then <c>Name</c>, with assignments and their members loaded. No activity or system-type
    /// filter. Teams with no role definitions are omitted.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<TeamRoleDefinition>>> GetAllRoleDefinitionsByTeamAsync(
        CancellationToken ct = default);

    /// <summary>Paged in the database: the include graph is too expensive to load wholesale.</summary>
    Task<(IReadOnlyList<Team> Items, int TotalCount)> GetAllForAdminAsync(
        int page, int pageSize, CancellationToken ct = default);

    /// <summary>Also loads referenced parents missing from the requested set.</summary>
    Task<IReadOnlyDictionary<Guid, Team>> GetByIdsWithParentsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default);

    Task<bool> HasActiveChildrenAsync(Guid teamId, CancellationToken ct = default);

    Task<IReadOnlyList<(Guid ChildId, Guid ParentId)>> GetActiveChildIdsByParentsAsync(
        IReadOnlyCollection<Guid> parentTeamIds, CancellationToken ct = default);

    /// <summary>Returns only known unique-constraint conflicts; other failures propagate.</summary>
    Task<TeamUpdateConflict> UpdateTeamAsync(Team team, CancellationToken ct = default);

    /// <summary>
    /// One save: the configured true sentinel preserves an explicit false on insert. Reports only named unique
    /// conflicts; slug conflicts drive the service's suffix retry.
    /// </summary>
    Task<TeamUpdateConflict> AddTeamWithRequiresApprovalOverrideAsync(
        Team team, bool requiresApproval, CancellationToken ct = default);

    /// <summary>Closes every active membership and deactivates the team in one transaction. Returns the users removed.</summary>
    Task<IReadOnlyList<Guid>> DeactivateTeamAsync(Guid teamId, Instant now, CancellationToken ct = default);

    /// <summary>Also deletes Teams-owned child rows. False when the team doesn't exist.</summary>
    Task<bool> PermanentlyDeleteTeamAsync(Guid teamId, CancellationToken ct = default);

    Task<(bool Updated, string? PreviousPrefix)> SetGoogleGroupPrefixAsync(
        Guid teamId, string? prefix, CancellationToken ct = default);

    /// <summary>With <c>Team</c> and <c>Team.ParentTeam</c>. Detached.</summary>
    Task<IReadOnlyList<TeamMember>> GetActiveByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Active non-system teams only.</summary>
    Task<IReadOnlyList<Guid>> GetUserCoordinatorTeamIdsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Direct department coordinatorships only (no parent).</summary>
    Task<IReadOnlyList<Guid>> GetUserDepartmentCoordinatorTeamIdsAsync(
        Guid userId, CancellationToken ct = default);

    Task<bool> IsActiveMemberAsync(Guid teamId, Guid userId, CancellationToken ct = default);

    /// <summary>Tracked, for writes that mutate it.</summary>
    Task<TeamMember?> FindActiveMemberForMutationAsync(
        Guid teamId, Guid userId, CancellationToken ct = default);

    Task AddMemberAsync(TeamMember member, CancellationToken ct = default);

    /// <summary>False when a unique-constraint violation shows the user is already an active member.</summary>
    Task<bool> TryAddMemberAsync(TeamMember member, CancellationToken ct = default);

    /// <summary>One transaction. False when the user is already an active member.</summary>
    Task<bool> ApproveRequestWithMemberAsync(
        TeamJoinRequest request,
        TeamMember member,
        CancellationToken ct = default);

    /// <summary>
    /// Removes the member's role assignments and sets <c>LeftAt</c>; returns the removed assignments. A merge may pass
    /// the survivor's same-team membership: assignments move there with slot and provenance, duplicates keep the
    /// survivor's, Coordinator status is kept, and the fold commits with the source's LeftAt.
    /// </summary>
    Task<IReadOnlyList<TeamRoleAssignment>> MarkMemberLeftAsync(
        Guid teamMemberId, Instant leftAt, Guid? mergeIntoMemberId = null, CancellationToken ct = default);

    /// <summary>No-op, returning null, when the user isn't an active member.</summary>
    Task<TeamMemberRole?> SetMemberRoleAsync(
        Guid teamId, Guid userId, TeamMemberRole role, CancellationToken ct = default);

    /// <summary>False when no pending request was found.</summary>
    Task<bool> WithdrawRequestAsync(
        Guid requestId, Guid userId, Instant now, CancellationToken ct = default);

    /// <summary>False when the request is missing or no longer pending.</summary>
    Task<bool> RejectRequestAsync(
        Guid requestId, Guid reviewerUserId, string reason, Instant now, CancellationToken ct = default);

    /// <summary>
    /// A non-null <paramref name="autoAddMember"/> adds the user to the team (with an outbox event). Also approves any
    /// pending join request.
    /// </summary>
    Task<(TeamRoleAssignment Assignment, bool AutoAddedToTeam, TeamMember Member)> AssignToRoleAsync(
        Guid roleDefinitionId,
        Guid targetUserId,
        Guid actorUserId,
        TeamMember? autoAddMember,
        bool promoteToCoordinator,
        Instant now,
        CancellationToken ct = default);

    /// <summary>Demotes from Coordinator when this was the member's last management assignment.</summary>
    Task<(bool Demoted, Guid TargetUserId)> UnassignFromRoleAsync(
        Guid roleDefinitionId, Guid teamMemberId, CancellationToken ct = default);

    /// <summary>
    /// Then demotes members who were coordinators only through this role. The caller invalidates the team cache when
    /// members were demoted.
    /// </summary>
    Task<(IReadOnlyList<Guid> DemotedUserIds, bool DemotedMembers)> PersistRoleDefinitionUpdateAsync(
        TeamRoleDefinition definition,
        bool clearingIsManagement,
        CancellationToken ct = default);

    /// <summary>Demotes coordinators server-side when <paramref name="clearingIsManagement"/>. Returns whether any were.</summary>
    Task<bool> PersistRoleIsManagementAsync(
        TeamRoleDefinition definition,
        bool clearingIsManagement,
        CancellationToken ct = default);

    Task<TeamJoinRequest?> FindUserPendingRequestAsync(
        Guid teamId, Guid userId, CancellationToken ct = default);

    Task<TeamJoinRequest?> FindRequestForMutationAsync(Guid requestId, CancellationToken ct = default);

    Task<TeamJoinRequest?> FindRequestWithTeamForMutationAsync(
        Guid requestId, CancellationToken ct = default);

    Task<IReadOnlyList<TeamJoinRequest>> GetPendingForTeamAsync(
        Guid teamId, CancellationToken ct = default);

    /// <summary>Teams not in the input are absent from the result.</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetPendingCountsByTeamIdsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default);

    Task AddRequestAsync(TeamJoinRequest request, CancellationToken ct = default);

    /// <summary>
    /// A source pending request to a team where the target also has one is dropped; every other row moves, so request
    /// history survives on the target.
    /// </summary>
    Task ReassignActiveJoinRequestsAsync(
        Guid sourceUserId, Guid targetUserId, CancellationToken ct = default);

    /// <summary>With assignments and their members. Detached.</summary>
    Task<IReadOnlyList<TeamRoleDefinition>> GetRoleDefinitionsAsync(
        Guid teamId, CancellationToken ct = default);

    /// <summary>Tracked, with Team, Assignments and Assignments.TeamMember.</summary>
    Task<TeamRoleDefinition?> FindRoleDefinitionForMutationAsync(
        Guid roleDefinitionId, CancellationToken ct = default);

    /// <summary>Tracked, with Team, Assignments and Assignments.TeamMember.</summary>
    Task<TeamRoleDefinition?> FindRoleDefinitionWithMembersForMutationAsync(
        Guid roleDefinitionId, CancellationToken ct = default);

    /// <summary>Matches on the lowercased name.</summary>
    Task<bool> RoleDefinitionNameExistsAsync(
        Guid teamId, string lowerName, Guid? excludingId, CancellationToken ct = default);

    Task<bool> OtherRoleHasIsManagementAsync(
        Guid teamId, Guid excludingRoleDefinitionId, CancellationToken ct = default);

    /// <summary>Teams without a public management role are absent.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetPublicManagementRoleNamesByTeamIdsAsync(
        IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default);

    Task AddRoleDefinitionAsync(TeamRoleDefinition definition, CancellationToken ct = default);

    Task RemoveRoleDefinitionAsync(TeamRoleDefinition definition, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetUserIdsWithManagementOnTeamAsync(
        Guid teamId, CancellationToken ct = default);

    /// <summary>Also removes their role assignments. Returns the number of memberships closed.</summary>
    Task<int> RevokeAllMembershipsAsync(Guid userId, Instant now, CancellationToken ct = default);

    Task<IReadOnlyList<(Guid TeamMemberId, Guid TeamId)>> GetActiveMembershipsForGoogleResyncAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>Only teams with EarlyEntryEnabled. Detached.</summary>
    Task<IReadOnlyList<TeamEarlyEntryGrant>> GetEarlyEntryGrantsForEnabledTeamsAsync(CancellationToken ct = default);

    /// <summary>Ordered by project then date. Detached.</summary>
    Task<IReadOnlyList<TeamEarlyEntryGrant>> GetEarlyEntryGrantsForTeamAsync(Guid teamId, CancellationToken ct = default);

    Task<IReadOnlyList<TeamEarlyEntryGrant>> GetEarlyEntryGrantsForUserAsync(Guid userId, CancellationToken ct = default);

    Task<TeamEarlyEntryGrant?> FindEarlyEntryGrantForMutationAsync(Guid grantId, CancellationToken ct = default);

    Task AddEarlyEntryGrantAsync(TeamEarlyEntryGrant grant, CancellationToken ct = default);

    Task UpdateEarlyEntryGrantAsync(TeamEarlyEntryGrant grant, CancellationToken ct = default);

    Task RemoveEarlyEntryGrantAsync(Guid grantId, CancellationToken ct = default);

    /// <summary>Leaves UpdatedAt untouched.</summary>
    Task ReassignEarlyEntryGrantsAsync(Guid sourceUserId, Guid targetUserId, CancellationToken ct = default);

    Task RemoveEarlyEntryGrantsForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Includes ended memberships.</summary>
    Task<IReadOnlyList<TeamMember>> GetAllMembershipsForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>Hard-deletes with state history: the request carries their free-text <c>Message</c>.</summary>
    Task<int> DeleteJoinRequestsForUserAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<TeamJoinRequest>> GetAllJoinRequestsForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>With role assignments, their definitions and the team. Detached.</summary>
    Task<IReadOnlyList<TeamMember>> GetActiveMembershipsForRoleReconciliationAsync(
        CancellationToken ct = default);

    /// <summary>Ignores team-member ids that are no longer active.</summary>
    Task<int> ApplyMemberRoleChangesAsync(
        IReadOnlyCollection<(Guid TeamMemberId, TeamMemberRole Role)> changes,
        CancellationToken ct = default);

    /// <summary>
    /// One save. Removing closes the active membership and deletes its role assignments. Returns true if anything was
    /// written.
    /// </summary>
    Task<bool> ApplySystemTeamMembershipDeltaAsync(
        Guid teamId,
        IReadOnlyCollection<Guid> userIdsToAdd,
        IReadOnlyCollection<Guid> userIdsToRemove,
        Instant now,
        CancellationToken ct = default);
}

internal enum TeamUpdateConflict { None, SlugTaken, CustomSlugTaken, GroupPrefixTaken }
