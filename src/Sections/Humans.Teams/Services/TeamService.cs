using Humans.GoogleIntegration.Contracts;
using Humans.Auth.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Base.Extensions;
using Humans.Base.Helpers;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces.Caching;
using Humans.EarlyEntry.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Notifications.Contracts;
using Humans.Teams.Data;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Teams.Domain;
using Humans.Base.Enums;
using Humans.Users.Contracts;

namespace Humans.Teams.Services;

internal sealed class TeamService(
    ITeamRepository repo,
    IAuditLogService auditLogService,
    INotificationEmitter notificationService,
    IShiftService shiftManagementService,
    IAdminAuthorizationService adminAuthorization,
    IServiceProvider serviceProvider,
    IClock clock,
    ILogger<TeamService> logger) : ITeamManagementService, ITeamSeeding, IGoogleGroupMembershipSource, IUserMerge, IEarlyEntryProvider
{
    private readonly TeamLifecycle lifecycle = new(repo, adminAuthorization, auditLogService, clock, logger, serviceProvider);
    private readonly TeamDataExport dataExport = new(repo, serviceProvider);
    private readonly TeamPageContent pageContentOwner = new(repo, clock, auditLogService, logger);
    private readonly TeamSnapshotReader snapshotReader = new(repo, serviceProvider, shiftManagementService);
    private TeamMembershipAdministration MembershipAdministration => new(
        repo, auditLogService, clock, logger, serviceProvider, snapshotReader, membershipOutbox, membershipNotices);
    private TeamJoining Joining => new(repo, auditLogService, clock, logger, snapshotReader, membershipOutbox, membershipNotices);
    private TeamRoleDefinitions RoleDefinitions => new(repo, auditLogService, clock, serviceProvider, snapshotReader, membershipOutbox);
    private TeamEarlyEntry EarlyEntry => new(repo, auditLogService, clock, snapshotReader, serviceProvider);
    private readonly TeamMembershipOutbox membershipOutbox = new(repo, serviceProvider, clock, logger);
    private readonly TeamMembershipNotices membershipNotices = new(notificationService, serviceProvider, logger);
    private readonly TeamGoogleGroupMemberships googleGroupMemberships = new(repo, logger);
    internal const string TeamMemberships = TeamDataExport.TeamMemberships;
    internal const string TeamJoinRequests = TeamDataExport.TeamJoinRequests;
    internal const string TeamEarlyEntry = TeamDataExport.TeamEarlyEntry;

    private IGoogleSyncOutboxService GoogleSyncOutboxService
        => serviceProvider.GetRequiredService<IGoogleSyncOutboxService>();

    private IGoogleSyncWrite GoogleSyncService
        => serviceProvider.GetRequiredService<IGoogleSyncWrite>();

    public Task<Team> CreateTeamAsync(
        string name, string? description, bool requiresApproval,
        Guid? parentTeamId = null, string? googleGroupPrefix = null, bool isHidden = false,
        CancellationToken cancellationToken = default) =>
        lifecycle.CreateTeamAsync(name, description, requiresApproval, parentTeamId,
            googleGroupPrefix, isHidden, cancellationToken);

    public async Task<TeamInfo?> GetTeamBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        var teamsById = await LoadTeamsByIdAsync(cancellationToken);
        return TeamSlugResolver.Find(teamsById, slug);
    }

    public Task<TeamInfo?> GetTeamAsync(Guid teamId, CancellationToken cancellationToken = default) =>
        snapshotReader.GetTeamAsync(teamId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamsAsync(
        CancellationToken cancellationToken = default) =>
        await LoadTeamsByIdAsync(cancellationToken);

    // Served from CachingTeamService's snapshot: reaching the inner service means a DI mistake.
    public Task<IReadOnlyList<TeamSearchHit>> SearchAsync(
        string query, int max,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Team search runs against the cached TeamInfo snapshot in CachingTeamService. " +
            "If this is being called on the inner TeamService it indicates a DI registration " +
            "mistake — ITeamService must resolve to the caching decorator.");

    public async Task<IReadOnlyCollection<Guid>> GetEffectiveBudgetCoordinatorTeamIdsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var departmentIds = await repo.GetUserDepartmentCoordinatorTeamIdsAsync(userId, cancellationToken);
        var teamIds = departmentIds.ToHashSet();

        if (teamIds.Count == 0)
            return teamIds;

        var childTeams = await repo.GetActiveChildIdsByParentsAsync(teamIds.ToList(), cancellationToken);
        foreach (var (childId, _) in childTeams)
            teamIds.Add(childId);

        return teamIds;
    }

    public async Task<(bool Updated, string? PreviousPrefix)> SetGoogleGroupPrefixAsync(
        Guid teamId, string? prefix, CancellationToken cancellationToken = default)
    {
        var (updated, previous) = await repo.SetGoogleGroupPrefixAsync(teamId, prefix, cancellationToken);
        return (updated, previous);
    }

    public Task<IReadOnlyList<UserTeamMembershipInfo>> GetUserTeamMembershipsAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        snapshotReader.GetUserTeamMembershipsAsync(userId, cancellationToken);

    public Task<TeamUpdateResult> UpdateTeamAsync(
        Guid teamId, string name, string? description, bool requiresApproval, bool isActive,
        Guid? parentTeamId = null, string? googleGroupPrefix = null, string? customSlug = null,
        bool? hasBudget = null, bool? isHidden = null, bool? isSensitive = null,
        bool? isPromotedToDirectory = null, bool? earlyEntryEnabled = null,
        CancellationToken cancellationToken = default) =>
        lifecycle.UpdateTeamAsync(teamId, name, description, requiresApproval, isActive,
            parentTeamId, googleGroupPrefix, customSlug, hasBudget, isHidden, isSensitive,
            isPromotedToDirectory, earlyEntryEnabled, cancellationToken);

    public Task<TeamPageUpdateResult> UpdateTeamPageContentAsync(
        Guid teamId,
        string? pageContent,
        IReadOnlyList<TeamPageCallToActionInput> callsToAction,
        bool isPublicPage,
        bool showCoordinatorsOnPublicPage,
        Guid updatedByUserId,
        CancellationToken cancellationToken = default) =>
        pageContentOwner.UpdateTeamPageContentAsync(teamId, pageContent, callsToAction,
            isPublicPage, showCoordinatorsOnPublicPage, updatedByUserId, cancellationToken);

    public async Task<TeamUpdateResult> CreateTeamWithGoogleGroupAsync(
        string name,
        string? description,
        bool requiresApproval,
        Guid? parentTeamId = null,
        string? googleGroupPrefix = null,
        bool isHidden = false,
        CancellationToken cancellationToken = default)
    {
        var result = await lifecycle.CreateTeamCoreAsync(
            name, description, requiresApproval, parentTeamId, googleGroupPrefix, isHidden, cancellationToken);
        if (!result.Succeeded) return result;
        var team = result.Team!;

        string? groupWarning = null;
        if (!string.IsNullOrEmpty(googleGroupPrefix))
        {
            // Provisioning reads Teams back through its cache before the outer mutation completes.
            serviceProvider.GetRequiredService<ITeamManagementService>().InvalidateActiveTeamsCache();
            try
            {
                var groupResult = await GoogleSyncService.EnsureTeamGroupAsync(team.Id, cancellationToken: cancellationToken);
                groupWarning = GroupLinkWarning(groupResult);
                if (groupWarning is not null)
                    logger.LogWarning("Google Group setup incomplete for new team {TeamId}: {Reason}", team.Id, groupWarning);
                if (groupResult.RequiresConfirmation)
                    return result with { GroupWarning = groupWarning };
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create Google Group for new team {TeamId}, clearing prefix", team.Id);
                groupWarning = ex.Message;
            }

            if (groupWarning is not null)
            {
                // Clear the prefix so the team never points at a missing group; the admin can retry from team edit.
                result = await UpdateTeamAsync(
                    team.Id, team.Name, team.Description, team.RequiresApproval, team.IsActive,
                    team.ParentTeamId, googleGroupPrefix: null, cancellationToken: cancellationToken);
                result.RequireSuccess();
                groupWarning = $"Team created but Google Group setup failed: {groupWarning}. The group prefix has been cleared.";
            }
        }

        return result with { GroupWarning = groupWarning };
    }

    public async Task<TeamUpdateResult> UpdateTeamWithGoogleGroupAsync(
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
        bool confirmGroupReactivation = false,
        Guid? confirmedInactiveGroupResourceId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await UpdateTeamAsync(
            teamId, name, description, requiresApproval, isActive, parentTeamId,
            googleGroupPrefix, customSlug, hasBudget, isHidden, isSensitive,
            isPromotedToDirectory, earlyEntryEnabled, cancellationToken);

        if (!result.Succeeded) return result;

        // The callback must see the saved prefix, including a cleared one, rather than the previous snapshot.
        serviceProvider.GetRequiredService<ITeamManagementService>().InvalidateActiveTeamsCache();
        string? groupWarning;
        Guid? inactiveGroupResourceId = null;
        try
        {
            var groupResult = await GoogleSyncService.EnsureTeamGroupAsync(teamId, cancellationToken: cancellationToken);
            if (confirmGroupReactivation && confirmedInactiveGroupResourceId is { } confirmedId
                && groupResult.RequiresConfirmation && groupResult.InactiveResourceId == confirmedId)
            {
                groupResult = await GoogleSyncService.EnsureTeamGroupAsync(teamId, true, cancellationToken);
            }
            groupWarning = GroupLinkWarning(groupResult);
            if (groupResult.RequiresConfirmation)
                inactiveGroupResourceId = groupResult.InactiveResourceId;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to sync Google Group for team {TeamId}", teamId);
            groupWarning = $"Team updated but Google Group setup failed: {ex.Message}";
        }

        return result with { GroupWarning = groupWarning, InactiveGroupResourceId = inactiveGroupResourceId };
    }

    private static string? GroupLinkWarning(GroupLinkResult result) =>
        result.Success
            ? null
            : result.RequiresConfirmation
                ? result.WarningMessage ?? "Confirmation required for group reactivation."
                : result.ErrorMessage ?? "Google Group linking failed.";

    public Task<TeamDeactivationResult> DeleteTeamAsync(
        Guid teamId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        lifecycle.DeleteTeamAsync(teamId, actorUserId, cancellationToken);

    public Task<TeamJoinResult> JoinTeamAsync(
        Guid teamId, Guid userId, string? message, CancellationToken cancellationToken = default) =>
        Joining.JoinTeamAsync(teamId, userId, message, cancellationToken);

    public Task<TeamJoinRequestResult> RequestToJoinTeamAsync(
        Guid teamId, Guid userId, string? message, CancellationToken cancellationToken = default) =>
        Joining.RequestToJoinTeamAsync(teamId, userId, message, cancellationToken);

    public Task<TeamDirectJoinResult> JoinTeamDirectlyAsync(
        Guid teamId, Guid userId, CancellationToken cancellationToken = default) =>
        Joining.JoinTeamDirectlyAsync(teamId, userId, cancellationToken);

    public Task<TeamMembershipMutationResult> LeaveTeamAsync(
        Guid teamId, Guid userId, CancellationToken cancellationToken = default) =>
        MembershipAdministration.LeaveTeamAsync(teamId, userId, cancellationToken);

    public Task<TeamMembershipMutationResult> WithdrawJoinRequestAsync(
        Guid requestId, Guid userId, CancellationToken cancellationToken = default) =>
        Joining.WithdrawJoinRequestAsync(requestId, userId, cancellationToken);

    public Task<TeamJoinReviewResult> ApproveJoinRequestAsync(
        Guid requestId, Guid approverUserId, string? notes, CancellationToken cancellationToken = default) =>
        Joining.ApproveJoinRequestAsync(requestId, approverUserId, notes, cancellationToken);

    public Task<TeamJoinReviewResult> RejectJoinRequestAsync(
        Guid requestId, Guid approverUserId, string reason, CancellationToken cancellationToken = default) =>
        Joining.RejectJoinRequestAsync(requestId, approverUserId, reason, cancellationToken);

    public Task<IReadOnlyList<TeamJoinRequestSnapshot>> GetPendingRequestsForTeamAsync(
        Guid teamId, CancellationToken cancellationToken = default) =>
        snapshotReader.GetPendingRequestsForTeamAsync(teamId, cancellationToken);

    public Task<IReadOnlyList<TeamJoinRequestSnapshot>> GetPendingRequestsForUserAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        snapshotReader.GetPendingRequestsForUserAsync(userId, cancellationToken);

    public Task<TeamJoinRequestSnapshot?> GetUserPendingRequestAsync(
        Guid teamId, Guid userId, CancellationToken cancellationToken = default) =>
        snapshotReader.GetUserPendingRequestAsync(teamId, userId, cancellationToken);

    public Task<bool> IsUserCoordinatorOfTeamAsync(
        Guid teamId, Guid userId, CancellationToken cancellationToken = default) =>
        snapshotReader.IsUserCoordinatorOfTeamAsync(teamId, userId, cancellationToken);

    public Task<TeamMemberRemovalResult> RemoveMemberAsync(
        Guid teamId, Guid userId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        MembershipAdministration.RemoveMemberAsync(teamId, userId, actorUserId, cancellationToken);

    public Task<TeamJoinReviewResult> AddMemberToTeamAsync(
        Guid teamId, Guid targetUserId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        MembershipAdministration.AddMemberToTeamAsync(teamId, targetUserId, actorUserId, cancellationToken);

    public Task SetMemberRoleAsync(
        Guid teamId, Guid userId, TeamMemberRole role, Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        MembershipAdministration.SetMemberRoleAsync(teamId, userId, role, actorUserId, cancellationToken);

    public Task<TeamRoleDefinitionMutationResult> CreateRoleDefinitionAsync(
        Guid teamId, string name, string? description, int slotCount,
        List<SlotPriority> priorities, int sortOrder, RolePeriod period, Guid actorUserId,
        bool isPublic = true, int? estimatedHours = null, CancellationToken cancellationToken = default) =>
        RoleDefinitions.CreateRoleDefinitionAsync(teamId, name, description, slotCount, priorities,
            sortOrder, period, actorUserId, isPublic, estimatedHours, cancellationToken);

    public Task<TeamRoleDefinitionMutationResult> UpdateRoleDefinitionAsync(
        Guid roleDefinitionId, string name, string? description, int slotCount,
        List<SlotPriority> priorities, int sortOrder, bool? isManagement, RolePeriod period, Guid actorUserId,
        bool isPublic = true, int? estimatedHours = null,
        CancellationToken cancellationToken = default) =>
        RoleDefinitions.UpdateRoleDefinitionAsync(roleDefinitionId, name, description, slotCount, priorities,
            sortOrder, isManagement, period, actorUserId, isPublic, estimatedHours, cancellationToken);

    public Task<TeamRoleDefinitionMutationResult> DeleteRoleDefinitionAsync(
        Guid roleDefinitionId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        RoleDefinitions.DeleteRoleDefinitionAsync(roleDefinitionId, actorUserId, cancellationToken);

    public Task<TeamRoleDefinitionMutationResult> ToggleRoleIsManagementAsync(
        Guid roleDefinitionId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        RoleDefinitions.ToggleRoleIsManagementAsync(roleDefinitionId, actorUserId, cancellationToken);

    public Task<IReadOnlyList<TeamRoleDefinitionSnapshot>> GetRoleDefinitionsAsync(
        Guid teamId, CancellationToken cancellationToken = default) =>
        snapshotReader.GetRoleDefinitionsAsync(teamId, cancellationToken);

    public Task<TeamRoleAssignmentMutationResult> AssignToRoleAsync(
        Guid roleDefinitionId, Guid targetUserId, Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        RoleDefinitions.AssignToRoleAsync(roleDefinitionId, targetUserId, actorUserId, cancellationToken);

    public Task<TeamRoleAssignmentMutationResult> UnassignFromRoleAsync(
        Guid roleDefinitionId, Guid teamMemberId, Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        RoleDefinitions.UnassignFromRoleAsync(roleDefinitionId, teamMemberId, actorUserId, cancellationToken);

    public Task<IReadOnlyList<Guid>> GetUserCoordinatedTeamIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        repo.GetUserCoordinatorTeamIdsAsync(userId, cancellationToken);

    public async Task EnqueueGoogleResyncForUserTeamsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetCurrentInstant();
        var memberships = await repo.GetActiveMembershipsForGoogleResyncAsync(userId, cancellationToken);
        var outboxEvents = memberships
            .Select(membership => new GoogleSyncOutboxEvent
            {
                Id = Guid.NewGuid(),
                EventType = GoogleSyncOutboxEventTypes.AddUserToTeamResources,
                TeamId = membership.TeamId,
                UserId = userId,
                OccurredAt = now,
                DeduplicationKey = $"{membership.TeamMemberId}:{GoogleSyncOutboxEventTypes.AddUserToTeamResources}:resync:{now}"
            })
            .ToList();

        await GoogleSyncOutboxService.AddRangeAsync(outboxEvents, cancellationToken);
        var count = outboxEvents.Count;
        if (count > 0)
        {
            logger.LogInformation(
                "Enqueued {Count} re-sync events for user {UserId} after Google email change",
                count, userId);
        }
    }

    async Task<TeamInfo> ITeamSeeding.CreateTeamAsync(
        string name,
        string? description,
        bool requiresApproval,
        Guid? parentTeamId,
        string? googleGroupPrefix,
        bool isHidden,
        CancellationToken cancellationToken)
    {
        var team = await CreateTeamAsync(
            name, description, requiresApproval, parentTeamId, googleGroupPrefix, isHidden,
            cancellationToken);
        return await GetTeamAsync(team.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Team {team.Id} missing from the read model after creation");
    }

    async Task ITeamSeeding.UpdateTeamAsync(
        Guid teamId,
        string name,
        string? description,
        bool requiresApproval,
        bool isActive,
        Guid? parentTeamId,
        string? googleGroupPrefix,
        string? customSlug,
        bool? hasBudget,
        bool? isHidden,
        bool? isSensitive,
        bool? isPromotedToDirectory,
        bool? earlyEntryEnabled,
        CancellationToken cancellationToken)
    {
        var result = await UpdateTeamAsync(
            teamId, name, description, requiresApproval, isActive, parentTeamId, googleGroupPrefix,
            customSlug, hasBudget, isHidden, isSensitive, isPromotedToDirectory, earlyEntryEnabled,
            cancellationToken);
        result.RequireSuccess();
    }

    Task ITeamSeeding.AddSeededMemberAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Instant joinedAt,
        CancellationToken cancellationToken) =>
        AddSeededMemberAsync(teamId, userId, role, joinedAt, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamsWithParentsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken = default)
    {
        // Deactivated teams included, as the entity-returning sibling promises its callers.
        var teamsById = await LoadTeamsByIdAsync(cancellationToken);
        var result = new Dictionary<Guid, TeamInfo>();
        foreach (var teamId in teamIds)
        {
            if (!teamsById.TryGetValue(teamId, out var team))
                continue;
            result[teamId] = team;
            if (team.ParentTeamId is { } parentTeamId
                && teamsById.TryGetValue(parentTeamId, out var parent))
            {
                result[parentTeamId] = parent;
            }
        }
        return result;
    }

    public async Task<TeamMember> AddSeededMemberAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Instant joinedAt,
        CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdAsync(teamId, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
            throw new InvalidOperationException("AddSeededMemberAsync cannot target system teams");

        var existing = await repo.IsActiveMemberAsync(teamId, userId, cancellationToken);
        if (existing)
            throw new InvalidOperationException("User is already a member of this team");

        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Role = role,
            JoinedAt = joinedAt,
        };

        await repo.AddMemberAsync(member, cancellationToken);

        return member;
    }

    public Task<bool> PermanentlyDeleteTeamAsync(
        Guid teamId, CancellationToken cancellationToken = default) =>
        lifecycle.PermanentlyDeleteTeamAsync(teamId, cancellationToken);

    public Task<IReadOnlyDictionary<Guid, string>> GetManagementRoleNamesByTeamIdsAsync(
        IEnumerable<Guid> teamIds,
        CancellationToken cancellationToken = default) =>
        repo.GetPublicManagementRoleNamesByTeamIdsAsync(teamIds.ToList(), cancellationToken);

    public Task<IReadOnlyList<AdminTeamSummary>> GetAdminTeamListAsync(CancellationToken cancellationToken = default) =>
        snapshotReader.GetAdminTeamListAsync(cancellationToken);

    // CachingTeamService owns the cache.
    public void InvalidateActiveTeamsCache() { }

    public Task<int> RevokeAllMembershipsAsync(Guid userId, CancellationToken cancellationToken = default) =>
        MembershipAdministration.RevokeAllMembershipsAsync(userId, cancellationToken);

    public Task ReassignAsync(
        Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken cancellationToken) =>
        MembershipAdministration.ReassignAsync(sourceUserId, targetUserId, actorUserId, updatedAt, cancellationToken);

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        dataExport.ContributeForUserAsync(userId, ct);

    /// <summary>Join requests are hard-deleted: they carry the applicant's free-text message.</summary>
    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        await RevokeAllMembershipsAsync(userId, ct);
        await repo.DeleteJoinRequestsForUserAsync(userId, ct);
        await DeleteEarlyEntryGrantsForUserAsync(userId, ct);

        // No cache call: Gdpr reaches this through CachingTeamService, which evicts.
    }

    public Task<IReadOnlyList<EarlyEntryGrant>> GetEarlyEntriesAsync(CancellationToken ct) =>
        EarlyEntry.GetEarlyEntriesAsync(ct);

    public Task<IReadOnlyList<TeamEarlyEntryGrantInfo>> GetEarlyEntryGrantsForTeamAsync(Guid teamId, CancellationToken ct = default) =>
        EarlyEntry.GetEarlyEntryGrantsForTeamAsync(teamId, ct);

    public Task<TeamEarlyEntryMutationResult> AddEarlyEntryGrantAsync(Guid teamId, Guid userId, LocalDate entryDate, string projectName, Guid actorUserId, Guid eventSettingsId, CancellationToken ct = default) =>
        EarlyEntry.AddEarlyEntryGrantAsync(teamId, userId, entryDate, projectName, actorUserId, eventSettingsId, ct);

    public Task<TeamEarlyEntryMutationResult> EditEarlyEntryGrantAsync(Guid teamId, Guid grantId, LocalDate entryDate, string projectName, Guid actorUserId, Guid eventSettingsId, CancellationToken ct = default) =>
        EarlyEntry.EditEarlyEntryGrantAsync(teamId, grantId, entryDate, projectName, actorUserId, eventSettingsId, ct);

    public Task RemoveEarlyEntryGrantAsync(Guid teamId, Guid grantId, Guid actorUserId, CancellationToken ct = default) =>
        EarlyEntry.RemoveEarlyEntryGrantAsync(teamId, grantId, actorUserId, ct);

    private Task DeleteEarlyEntryGrantsForUserAsync(Guid userId, CancellationToken ct) =>
        EarlyEntry.DeleteEarlyEntryGrantsForUserAsync(userId, ct);

    private Task<IReadOnlyDictionary<Guid, TeamInfo>> LoadTeamsByIdAsync(CancellationToken ct = default) =>
        snapshotReader.LoadTeamsByIdAsync(ct);

    public Task<IReadOnlyDictionary<Guid, TeamInfo>> ResolveTeamMembersAsync(
        IReadOnlyDictionary<Guid, TeamReadSnapshot> teams, CancellationToken ct = default) =>
        snapshotReader.ResolveTeamMembersAsync(teams, ct);

    public Task<IReadOnlyDictionary<Guid, TeamReadSnapshot>> GetTeamSnapshotsAsync(CancellationToken ct = default) =>
        snapshotReader.GetTeamSnapshotsAsync(ct);

    public async Task<IReadOnlyList<TeamRoleReconciliationMembership>> GetActiveMembershipsForRoleReconciliationAsync(
        CancellationToken cancellationToken = default)
    {
        var memberships = await repo.GetActiveMembershipsForRoleReconciliationAsync(cancellationToken);
        return memberships
            .Select(member => new TeamRoleReconciliationMembership(
                member.Id,
                member.UserId,
                member.TeamId,
                member.Team.Name,
                member.Role,
                member.Team.SystemTeamType,
                member.RoleAssignments.Any(assignment => assignment.TeamRoleDefinition.IsManagement)))
            .ToList();
    }

    public async Task<IReadOnlyList<Guid>> ApplyMemberRoleChangesAsync(
        IReadOnlyCollection<(Guid TeamMemberId, TeamMemberRole Role)> changes,
        CancellationToken cancellationToken = default)
    {
        if (changes.Count == 0)
            return [];
        return await repo.ApplyMemberRoleChangesAsync(changes, cancellationToken);
    }

    public async Task<bool> ApplySystemTeamMembershipDeltaAsync(
        Guid teamId,
        IReadOnlyCollection<Guid> userIdsToAdd,
        IReadOnlyCollection<Guid> userIdsToRemove,
        Instant now,
        CancellationToken cancellationToken = default)
    {
        return await repo.ApplySystemTeamMembershipDeltaAsync(
            teamId, userIdsToAdd, userIdsToRemove, now, cancellationToken);
    }

    public Task<Dictionary<string, Guid[]>> GetExpectedAsync(
        string? groupKey = null,
        CancellationToken ct = default) =>
        googleGroupMemberships.GetExpectedAsync(groupKey, ct);

}
