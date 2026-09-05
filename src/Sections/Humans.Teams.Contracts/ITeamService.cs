using Humans.Users.Contracts;
using Humans.Base.Interfaces;
using Humans.Base.Constants;
using Humans.Base.Enums;
using NodaTime;

namespace Humans.Teams.Contracts;

/// <summary>
/// Cached team read-model used by <c>CachingTeamService</c> to project
/// every Teams-section read entirely from memory.
/// </summary>
/// <remarks>
/// The whole graph is a few dozen teams with their members, role definitions and page
/// markdown — well under a megabyte — so it is held in full and re-warmed wholesale.
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
/// Returned by <see cref="ITeamServiceRead.GetUserTeamMembershipsAsync"/> as a flat
/// membership projection.
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
/// The half of the Teams service that lives outside the section: the members Base and other
/// sections call, expressed in flat projections so no Teams entity leaves the assembly. The
/// section's own management surface is on the internal <c>ITeamManagementService</c>, which
/// inherits this. A member with no caller outside the assembly does not belong here.
/// </summary>
public interface ITeamService : ITeamServiceRead, IApplicationService
{
    /// <summary>
    /// Sets <c>Team.GoogleGroupPrefix</c> to <paramref name="prefix"/> (may be
    /// null to clear) and persists the change. Returns the previous prefix so
    /// callers can revert on downstream-service failure. Returns (<c>false</c>,
    /// <c>null</c>) if the team does not exist. Narrow alternative to the
    /// management surface's <c>UpdateTeamAsync</c> for flows that only touch the
    /// Google-group wiring; GoogleIntegration is the caller.
    /// </summary>
    Task<(bool Updated, string? PreviousPrefix)> SetGoogleGroupPrefixAsync(
        Guid teamId, string? prefix, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the role of an active membership. Development's persona seeder is the
    /// caller. No-op if the user has no active membership on the team.
    /// </summary>
    Task SetMemberRoleAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues AddUserToTeamResources sync events for all active team
    /// memberships of a user. GoogleIntegration calls it when the user's Google
    /// service email changes.
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
    // Cache Helpers
    // ==========================================================================

    /// <summary>
    /// Drops the cached team graph after a user's memberships were changed underneath
    /// the service (Users' account deletion). The inner service has no cache; only the
    /// decorator does anything here.
    /// </summary>
    void RemoveMemberFromAllTeamsCache(Guid userId);

    /// <summary>
    /// Drops the cached team graph so the next read repopulates from the database.
    /// For callers that wrote around the service or rolled a transaction back — Users
    /// evicts through <c>IActiveTeamsCacheInvalidator</c>. The inner service has no
    /// cache; only the decorator does anything here.
    /// </summary>
    void InvalidateActiveTeamsCache();

    /// <summary>
    /// Ends all active team memberships for a user, removes their team role assignments,
    /// and returns the count of ended memberships. Used during account deletion.
    /// </summary>
    Task<int> RevokeAllMembershipsAsync(Guid userId, CancellationToken cancellationToken = default);

    // ==========================================================================
    // System team sync support — the bulk-apply protocol used by SystemTeamSyncJob and
    // by Development's persona seeder. Each call commits in its own repository-owned
    // unit of work; the caller fans out Google sync and audit calls afterwards.
    // ==========================================================================

    /// <summary>
    /// Applies a system-team membership reconciliation in a single save:
    /// inserts new membership rows for <paramref name="userIdsToAdd"/>
    /// with <see cref="TeamMemberRole.Member"/> + <c>JoinedAt=now</c>, and
    /// soft-removes the active memberships for <paramref name="userIdsToRemove"/>
    /// by stamping <c>LeftAt</c> and cascade-deleting any attached role
    /// assignments. Bumps the team's <c>UpdatedAt</c> and invalidates the cache
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
/// because these three are the only callers and none of them is a production path.
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
