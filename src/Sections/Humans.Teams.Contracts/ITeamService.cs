using Humans.Users.Contracts;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Teams.Contracts;

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
    int PendingRequestCount = 0,
    bool EarlyEntryEnabled = false)
{
    /// <summary>
    /// Full Google Group email address, or null if no prefix is set. Mirrors
    /// the canonical formula on the <c>Team</c> entity's own <c>GoogleGroupEmail</c>
    /// so callers stitching via the cache get the same value without touching it.
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

public record TeamCoordinatorRef(Guid TeamId, Guid UserId);

public record TeamRoleReconciliationMembership(
    Guid TeamMemberId,
    Guid UserId,
    Guid TeamId,
    string TeamName,
    TeamMemberRole Role,
    SystemTeamType SystemTeamType,
    bool HasManagementRoleAssignment);

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
/// One active team membership of a user, flattened so cross-section callers can read the
/// team's identity without the <c>TeamMember</c> entity or its <c>Team</c> navigation.
/// Returned by <see cref="ITeamServiceRead.GetUserTeamMembershipsAsync"/> — the projection the
/// Teams-internal <c>GetUserTeamsAsync</c> hands its own callers as entities.
/// </summary>
public sealed record UserTeamMembershipInfo(
    Guid TeamMemberId,
    Guid TeamId,
    string TeamName,
    string TeamSlug,
    bool IsSystemTeam,
    TeamMemberRole Role,
    Instant JoinedAt);

/// <summary>
/// The half of the Teams service that lives outside the section: everything Base, Shell and
/// the Budget / Consent / Development / Expenses sections call, expressed in flat projections
/// so no Teams entity leaves the assembly. The section's own ~40-member management surface is
/// on the internal <c>ITeamManagementService</c>, which inherits this
/// (design §15 step 5b — carve the leaf from the call sites, not the interface).
/// </summary>
public interface ITeamService : ITeamServiceRead, IApplicationService
{


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
    /// Sets the <see cref="TeamMember.Role"/> for an active membership to the
    /// given value. Used by the account-merge fan-out to preserve a coordinator
    /// role when migrating the membership from the archived source account to
    /// the target. No-op if the user has no active membership on the team.
    /// </summary>
    Task SetMemberRoleAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    // ==========================================================================
    // Coordinator Queries
    // ==========================================================================

    /// <summary>
    /// Returns the active (non-Volunteers) teams the user belongs to with the
    /// user's role on each team. Callers that only need names project via
    /// <c>.Select(m =&gt; m.TeamName)</c>. Display ordering is the caller's
    /// responsibility (rendering layer).
    /// </summary>
    Task<IReadOnlyList<TeamMembership>> GetActiveTeamMembershipsForUserAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues AddUserToTeamResources sync events for all active team
    /// memberships of a user. Used when the user's Google service email changes.
    /// </summary>
    Task EnqueueGoogleResyncForUserTeamsAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently deletes a team and its Teams-owned child rows. Requires the
    /// current authenticated user to hold the full Admin role. The team must
    /// have no linked Google resources — <c>GoogleResource → Team</c> is
    /// configured with <c>OnDelete(Restrict)</c>, so the caller must unlink
    /// resources via <c>ITeamResourceService</c> first.
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

    // ==========================================================================
    // System team sync support (issue #570 — §15 Google-writing jobs)
    //
    // Narrow read/write methods used exclusively by SystemTeamSyncJob so the
    // job can drop its DbContext dependency. Each mutation commits in
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

    /// <summary>Deletes every early-entry grant belonging to a user (right-to-erasure).</summary>
    Task DeleteEarlyEntryGrantsForUserAsync(Guid userId, CancellationToken ct = default);


}

public sealed record TeamRoleDefinitionSnapshot(
    Guid Id,
    Guid TeamId,
    string TeamName,
    string TeamSlug,
    string Name,
    string? Description,
    int SlotCount,
    int? EstimatedHours,
    IReadOnlyList<SlotPriority> Priorities,
    int SortOrder,
    bool IsManagement,
    RolePeriod Period,
    bool IsPublic,
    IReadOnlyList<TeamRoleAssignmentSnapshot> Assignments);

public sealed record TeamRoleAssignmentSnapshot(
    Guid Id,
    Guid TeamMemberId,
    int SlotIndex,
    Guid? AssignedUserId);


/// <summary>
/// The narrow team-provisioning surface the dev/demo fixture seeders drive —
/// <c>DevPersonaSeeder</c> and <c>DevelopmentDashboardSeeder</c> in <c>Humans.Development</c>
/// and <c>DevelopmentBudgetSeeder</c> in <c>Humans.Budget</c>, all of which build multi-section
/// fixtures and so cannot be pulled into this section. Kept off <see cref="ITeamService"/>
/// because these three are the only callers and none of them is a production path
/// (design §15 step 5b, Budget's dev-seeder rule).
/// </summary>
/// <remarks>
/// Implemented explicitly by the section's caching decorator: the Teams-internal members of the
/// same names return the <c>Team</c> / <c>TeamMember</c> entities, which cannot leave the
/// assembly, so these overloads differ only in their projection.
/// </remarks>
public interface ITeamSeeding
{
    /// <summary>Creates a team and returns its read model.</summary>
    Task<TeamInfo> CreateTeamAsync(
        string name,
        string? description,
        bool requiresApproval,
        Guid? parentTeamId = null,
        string? googleGroupPrefix = null,
        bool isHidden = false,
        CancellationToken cancellationToken = default);

    /// <summary>Updates a team's details.</summary>
    Task UpdateTeamAsync(
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
    /// Adds a team member with an explicit role and join timestamp, without audit entries,
    /// outbox events or user-facing email. Seed-only; production membership changes go through
    /// the section's own management surface. Throws if the user is already an active member.
    /// </summary>
    Task AddSeededMemberAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Instant joinedAt,
        CancellationToken cancellationToken = default);
}
