using Humans.Base.Interfaces;
using Humans.Base.Constants;
using Humans.Base.Enums;
using NodaTime;

namespace Humans.Teams.Contracts;

/// <summary>Includes active members. The whole graph is held in memory and re-warmed wholesale.</summary>
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
    /// <summary>Null when no prefix is set. Same formula as the <c>Team</c> entity's own.</summary>
    public string? GoogleGroupEmail =>
        GoogleGroupPrefix is null
            ? null
            : $"{GoogleGroupPrefix}@{DomainConstants.GoogleGroupDomain}";

    /// <summary>Only departments (non-system, top-level) may have a public page.</summary>
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

/// <summary>One active membership, with the team's identity stitched in.</summary>
public sealed record UserTeamMembershipInfo(
    Guid TeamMemberId,
    Guid TeamId,
    string TeamName,
    string TeamSlug,
    bool IsSystemTeam,
    TeamMemberRole Role,
    Instant JoinedAt);

/// <summary>Teams' cross-section reads. Writes are on <see cref="ITeamWrite"/>.</summary>
public interface ITeamService : IApplicationService
{
    /// <summary>Keyed by id, including active members.</summary>
    Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamsAsync(CancellationToken cancellationToken = default);

    Task<TeamInfo?> GetTeamAsync(Guid teamId, CancellationToken cancellationToken = default);

    /// <summary>Matches the canonical <c>Slug</c> or the optional <c>CustomSlug</c>.</summary>
    Task<TeamInfo?> GetTeamBySlugAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Active, non-hidden teams whose name contains <paramref name="query"/>, case-insensitive. At most
    /// <paramref name="max"/>, in no particular order: the global search ranks them.
    /// </summary>
    Task<IReadOnlyList<TeamSearchHit>> SearchAsync(
        string query, int max,
        CancellationToken cancellationToken = default);

    /// <summary>Non-system teams where the user is a coordinator or holds a management role.</summary>
    Task<IReadOnlyList<Guid>> GetUserCoordinatedTeamIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Top-level teams where the user is a direct coordinator or holds a management role, plus their active children.
    /// </summary>
    Task<IReadOnlyCollection<Guid>> GetEffectiveBudgetCoordinatorTeamIdsAsync(
        Guid userId, CancellationToken cancellationToken = default);

    Task<bool> IsUserCoordinatorOfTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>Active memberships only.</summary>
    Task<IReadOnlyList<UserTeamMembershipInfo>> GetUserTeamMembershipsAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Also loads referenced parents, so the caller can resolve each team's department. Not active-filtered: other
    /// sections' rows may still name deactivated teams.
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
/// The dev/demo fixture seeders' provisioning surface, kept off <see cref="ITeamService"/> because no production path
/// calls it.
/// </summary>
public interface ITeamSeeding
{
    Task<TeamInfo> CreateTeamAsync(
        string name,
        string? description,
        bool requiresApproval,
        Guid? parentTeamId = null,
        string? googleGroupPrefix = null,
        bool isHidden = false,
        CancellationToken cancellationToken = default);

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
    /// Seed only: no audit entries, outbox events or email. Throws if the user is already an active member.
    /// </summary>
    Task AddSeededMemberAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Instant joinedAt,
        CancellationToken cancellationToken = default);
}
