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

    /// <summary>
    /// Whether the team may have a public page: only departments (non-system,
    /// top-level teams). Mirrors the rule the team-page save enforces.
    /// </summary>
    public bool CanBePublic => !IsSystemTeam && ParentTeamId is null;
}

public record TeamMemberInfo(
    Guid TeamMemberId, Guid UserId, string DisplayName,
    string? Email, string? ProfilePictureUrl, TeamMemberRole Role, Instant JoinedAt);

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
    TeamMemberRole Role,
    Instant JoinedAt);

/// <summary>
/// One active team membership of a user, flattened so cross-section callers can read the
/// team's identity without the <c>TeamMember</c> entity or its <c>Team</c> navigation.
/// Returned by <see cref="ITeamService.GetUserTeamMembershipsAsync"/> as a flat
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
/// Public read contract of the Teams section. Other sections inject this
/// interface; only <see cref="TeamInfo"/> / <see cref="TeamSearchHit"/>
/// projections, no EF entities. Writes live on <see cref="ITeamWrite"/>.
/// </summary>
public interface ITeamService : IApplicationService
{
    /// <summary>
    /// Gets team read models keyed by ID, including active members.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the team read model by ID, including active members.
    /// </summary>
    Task<TeamInfo?> GetTeamAsync(Guid teamId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the team read model by slug (matches the canonical
    /// <c>Slug</c> or the optional <c>CustomSlug</c>).
    /// </summary>
    Task<TeamInfo?> GetTeamBySlugAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Active, non-hidden teams whose <c>Name</c> contains
    /// <paramref name="query"/> (case-insensitive). Capped at
    /// <paramref name="max"/>; returned in unspecified order — the global
    /// search orchestrator scores and ranks.
    /// </summary>
    Task<IReadOnlyList<TeamSearchHit>> SearchAsync(
        string query, int max,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all non-system team IDs where the user is a coordinator or has a management role.
    /// Used by shift services for authorization without exposing Teams-owned rows.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUserCoordinatedTeamIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the department-scoped team IDs the user can coordinate for budget purposes:
    /// top-level teams where the user is a direct coordinator or holds a management role,
    /// plus their active child teams.
    /// </summary>
    Task<IReadOnlyCollection<Guid>> GetEffectiveBudgetCoordinatorTeamIdsAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a user is a coordinator of a team.
    /// </summary>
    Task<bool> IsUserCoordinatorOfTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The user's active team memberships as flat rows — the cross-section projection of the
    /// the Teams section's internal membership data, without returning <c>TeamMember</c>
    /// entities.
    /// Same data source and same active-only filter; the team's name and slug are stitched in
    /// so the caller never navigates <c>TeamMember.Team</c>.
    /// </summary>
    Task<IReadOnlyList<UserTeamMembershipInfo>> GetUserTeamMembershipsAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the teams for the requested IDs <b>and</b> any referenced parent teams, so the
    /// caller can resolve the "department" (parent or self) for each team via dictionary
    /// lookups. Returned teams are not active-filtered — other sections' rows may still
    /// reference deactivated teams and the caller still needs the name.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamsWithParentsAsync(
        IReadOnlyCollection<Guid> teamIds,
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
