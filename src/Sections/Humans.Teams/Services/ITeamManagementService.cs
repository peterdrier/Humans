using Humans.Base.Attributes;
using Humans.Base.Enums;
using Humans.Gdpr.Contracts;
using Humans.Teams.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Domain;
using NodaTime;

namespace Humans.Teams.Services;

internal sealed record TeamPageCallToActionInput(string? Text, string? Url, CallToActionStyle Style);

internal sealed record TeamPageUpdateResult(bool Succeeded, string? ErrorMessage)
{
    public static TeamPageUpdateResult Success() => new(true, null);

    public static TeamPageUpdateResult Failed(string message) => new(false, message);
}

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
    string? ReviewNotes,
    string? TeamSlug = null);

/// <summary>The caller resolves the display name from <see cref="UserId"/>.</summary>
internal sealed record TeamEarlyEntryGrantInfo(
    Guid Id,
    Guid UserId,
    LocalDate EntryDate,
    string ProjectName);

/// <summary>
/// Internal: the cross-section half is <see cref="ITeamService"/> and <see cref="ITeamWrite"/>, inherited through
/// <see cref="ITeamManagement"/> so controllers and the caching decorator keep one injection.
/// </summary>
internal interface ITeamManagementService : ITeamManagement
{
    /// <summary><c>CachingTeamService</c> is the registered contributor.</summary>
    Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct);

    /// <summary>Reached through <c>CachingTeamService</c>, which evicts after it.</summary>
    Task EraseForUserAsync(Guid userId, CancellationToken ct);

    Task<Team> CreateTeamAsync(
        string name,
        string? description,
        bool requiresApproval,
        Guid? parentTeamId = null,
        string? googleGroupPrefix = null,
        bool isHidden = false,
        CancellationToken cancellationToken = default);

    /// <summary>Returns expected validation and unique-constraint refusals.</summary>
    Task<TeamUpdateResult> UpdateTeamAsync(
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
    /// When provisioning the Google Group fails, the prefix is cleared so the team never points at a missing group;
    /// <c>GroupWarning</c> then carries the operator message.
    /// </summary>
    [ExternalWrite]
    Task<TeamWithGroupResult> CreateTeamWithGoogleGroupAsync(
        string name,
        string? description,
        bool requiresApproval,
        Guid? parentTeamId = null,
        string? googleGroupPrefix = null,
        bool isHidden = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles the Google Group link only after a successful update. <c>GroupWarning</c> carries the operator message
    /// when the sync failed or needs reactivation confirmation; the update itself has succeeded.
    /// </summary>
    [ExternalWrite]
    Task<TeamUpdateResult> UpdateTeamWithGoogleGroupAsync(
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

    /// <summary>Deactivates; nothing is deleted.</summary>
    Task DeleteTeamAsync(Guid teamId, Guid actorUserId, CancellationToken cancellationToken = default);

    /// <summary>Approval-required teams get a pending request, open teams an immediate membership.</summary>
    Task<TeamJoinResult> JoinTeamAsync(
        Guid teamId,
        Guid userId,
        string? message,
        CancellationToken cancellationToken = default);

    Task<TeamMembershipMutationResult> LeaveTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<TeamMembershipMutationResult> WithdrawJoinRequestAsync(
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<TeamMember> ApproveJoinRequestAsync(
        Guid requestId,
        Guid approverUserId,
        string? notes,
        CancellationToken cancellationToken = default);

    Task RejectJoinRequestAsync(
        Guid requestId,
        Guid approverUserId,
        string reason,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TeamJoinRequestSnapshot>> GetPendingRequestsForTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TeamJoinRequestSnapshot>> GetPendingRequestsForUserAsync(
        Guid userId, CancellationToken cancellationToken = default);

    Task<TeamJoinRequestSnapshot?> GetUserPendingRequestAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles a removed coordinator's Coordinators system-team membership in the same mutation. Known refusals
    /// return resource keys; dependency failures propagate.
    /// </summary>
    Task<TeamMemberRemovalResult> RemoveMemberAsync(
        Guid teamId,
        Guid userId,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Teams without a management role are absent.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetManagementRoleNamesByTeamIdsAsync(
        IEnumerable<Guid> teamIds,
        CancellationToken cancellationToken = default);

    /// <summary>Ordered, ready for projection.</summary>
    Task<AdminTeamListResult> GetAdminTeamListAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>Bypasses the join-request workflow.</summary>
    Task<TeamMember> AddMemberToTeamAsync(
        Guid teamId,
        Guid targetUserId,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    Task<TeamPageUpdateResult> UpdateTeamPageContentAsync(
        Guid teamId,
        string? pageContent,
        IReadOnlyList<TeamPageCallToActionInput> callsToAction,
        bool isPublicPage,
        bool showCoordinatorsOnPublicPage,
        Guid updatedByUserId,
        CancellationToken cancellationToken = default);

    Task<TeamRoleDefinition> CreateRoleDefinitionAsync(
        Guid teamId, string name, string? description, int slotCount,
        List<SlotPriority> priorities, int sortOrder, RolePeriod period, Guid actorUserId,
        bool isPublic = true,
        int? estimatedHours = null,
        CancellationToken cancellationToken = default);

    Task<TeamRoleDefinition> UpdateRoleDefinitionAsync(
        Guid roleDefinitionId, string name, string? description, int slotCount,
        List<SlotPriority> priorities, int sortOrder, bool isManagement, RolePeriod period, Guid actorUserId,
        bool isPublic = true,
        bool canToggleManagement = true,
        int? estimatedHours = null,
        CancellationToken cancellationToken = default);

    /// <summary>Also deletes its assignments.</summary>
    Task DeleteRoleDefinitionAsync(
        Guid roleDefinitionId, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Can't be enabled while members are assigned to the role.</summary>
    Task<TeamRoleManagementToggleResult> ToggleRoleIsManagementAsync(
        Guid roleDefinitionId, Guid actorUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TeamRoleDefinitionSnapshot>> GetRoleDefinitionsAsync(
        Guid teamId, CancellationToken cancellationToken = default);

    /// <summary>Next free slot; then reconciles the target's Coordinators system-team membership.</summary>
    Task<TeamRoleAssignment> AssignToRoleAsync(
        Guid roleDefinitionId, Guid targetUserId, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Then reconciles the target's Coordinators system-team membership.</summary>
    Task UnassignFromRoleAsync(
        Guid roleDefinitionId, Guid teamMemberId, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Seed and migration only: no audit, outbox events or emails. Production changes go through
    /// <see cref="AddMemberToTeamAsync"/>, <see cref="ApproveJoinRequestAsync"/> or role assignment. Throws if already a
    /// member.
    /// </summary>
    Task<TeamMember> AddSeededMemberAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Instant joinedAt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TeamRoleReconciliationMembership>> GetActiveMembershipsForRoleReconciliationAsync(
        CancellationToken cancellationToken = default);

    /// <summary>One save. Returns the number of memberships updated.</summary>
    Task<int> ApplyMemberRoleChangesAsync(
        IReadOnlyCollection<(Guid TeamMemberId, TeamMemberRole Role)> changes,
        CancellationToken cancellationToken = default);

    /// <summary>Unordered: display order is the caller's.</summary>
    Task<IReadOnlyList<TeamEarlyEntryGrantInfo>> GetEarlyEntryGrantsForTeamAsync(Guid teamId, CancellationToken ct = default);

    /// <summary>Throws unless the team has <see cref="Team.EarlyEntryEnabled"/>.</summary>
    Task AddEarlyEntryGrantAsync(Guid teamId, Guid userId, LocalDate entryDate, string projectName, Guid actorUserId, CancellationToken ct = default);

    /// <summary>A grant on another team counts as not found.</summary>
    Task EditEarlyEntryGrantAsync(Guid teamId, Guid grantId, LocalDate entryDate, string projectName, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Idempotent. A grant on another team counts as not found.</summary>
    Task RemoveEarlyEntryGrantAsync(Guid teamId, Guid grantId, Guid actorUserId, CancellationToken ct = default);
}

internal sealed record TeamRoleReconciliationMembership(
    Guid TeamMemberId,
    Guid UserId,
    Guid TeamId,
    string TeamName,
    TeamMemberRole Role,
    SystemTeamType SystemTeamType,
    bool HasManagementRoleAssignment);

internal sealed record TeamRoleManagementToggleResult(
    string RoleName,
    bool IsManagement);

/// <summary><see cref="GroupWarning"/> is null when the group is in order, else the operator message to show.</summary>
internal sealed record TeamWithGroupResult(Team Team, string? GroupWarning);

internal enum TeamJoinOutcome
{
    Joined,

    RequestSubmitted,
}

internal sealed record TeamUpdateResult(
    Team? Team = null,
    TeamUpdateConflict Conflict = TeamUpdateConflict.None,
    string? ErrorMessage = null,
    string? GroupWarning = null)
{
    public bool Succeeded => Team is not null && Conflict == TeamUpdateConflict.None && ErrorMessage is null;

    // Non-interactive seed/compensation callers must not silently discard a refusal.
    public void RequireSuccess()
    {
        if (!Succeeded) throw new InvalidOperationException(ErrorMessage ?? $"Team update rejected: {Conflict}");
    }
}

internal sealed record TeamMemberRemovalResult(string? ErrorKey = null, string? ErrorMessage = null);

internal sealed record TeamMembershipMutationResult(bool Succeeded, string? ErrorKey = null, bool WasCoordinator = false);

internal sealed record TeamJoinResult(TeamJoinOutcome? Outcome = null, string? ErrorKey = null);
internal sealed record TeamJoinRequestResult(TeamJoinRequest? Request = null, string? ErrorKey = null);
internal sealed record TeamDirectJoinResult(TeamMember? Member = null, string? ErrorKey = null);
