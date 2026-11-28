using System.Text;
using Humans.Roles.Contracts;
using Humans.Permissions.Contracts;
using Humans.Base.Constants;
using Humans.Google.Contracts;
using Humans.Auth.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Base.Extensions;
using Humans.Base.Helpers;
using Humans.Audit.Contracts;
using Humans.EarlyEntry.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Notifications.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Contracts;
using Humans.Teams.Domain;
using Humans.Base.Enums;
using Humans.Users.Contracts;
using Humans.Settings.Contracts;
using Humans.Base.Threading;
using System.Globalization;
using System.Resources;
using Humans.Email.Contracts;
using System.Transactions;

namespace Humans.Teams.Services;

internal sealed class TeamService(
    ITeamRepository repo,
    IAuditLogService auditLogService,
    INotificationEmitter notificationService,
    IAdminAuthorizationService adminAuthorization,
    IServiceProvider serviceProvider,
    IClock clock,
    ILogger<TeamService> logger) : ITeamManagementService, ITeamSeeding, IGoogleGroupMembershipSource, IUserMerge, IEarlyEntryProvider
{
    private IGoogleSyncOutboxService GoogleSyncOutboxService
        => serviceProvider.GetRequiredService<IGoogleSyncOutboxService>();

    private IGoogleSyncWrite GoogleSyncService
        => serviceProvider.GetRequiredService<IGoogleSyncWrite>();

    public async Task<TeamInfo?> GetTeamBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        var teamsById = await LoadTeamsByIdAsync(cancellationToken);
        return TeamSlugResolver.Find(teamsById, slug);
    }

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

    public async Task<TeamUpdateResult> CreateTeamWithGoogleGroupAsync(
        string name,
        string? description,
        bool requiresApproval,
        Guid? parentTeamId = null,
        string? googleGroupPrefix = null,
        bool isHidden = false,
        CancellationToken cancellationToken = default)
    {
        var result = await CreateTeamCoreAsync(
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

    // CachingTeamService owns the cache.
    public void InvalidateActiveTeamsCache() { }

    /// <summary>Join requests are hard-deleted: they carry the applicant's free-text message.</summary>
    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        await RevokeAllMembershipsAsync(userId, ct);
        await repo.DeleteJoinRequestsForUserAsync(userId, ct);
        await DeleteEarlyEntryGrantsForUserAsync(userId, ct);

        // No cache call: Gdpr reaches this through CachingTeamService, which evicts.
    }

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

    /// <summary>
    /// The Asociados, Colaboradores and Board teams. Governance owns these bodies and claims their Google group and
    /// files, so Teams neither claims nor syncs them, and they are only ever retired.
    /// </summary>
    internal static readonly IReadOnlySet<Guid> GovernanceBodyTeamIds =
        new HashSet<Guid> { SystemTeamIds.Asociados, SystemTeamIds.Colaboradors, SystemTeamIds.Board };

    internal const string TeamMemberships = "TeamMemberships";
    internal const string TeamJoinRequests = "TeamJoinRequests";
    internal const string TeamEarlyEntry = "TeamEarlyEntry";

    // Lazy: Users depends on Teams during service resolution.
    private IUserService UserService => serviceProvider.GetRequiredService<IUserService>();

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var info = await UserService.GetUserInfoAsync(userId, ct);
        List<TeamMember> memberships = [];
        List<TeamJoinRequest> joinRequests = [];
        List<TeamEarlyEntryGrant> eeGrants = [];
        foreach (var id in info?.AllUserIds ?? [userId])
        {
            memberships.AddRange(await repo.GetAllMembershipsForUserAsync(id, ct));
            joinRequests.AddRange(await repo.GetAllJoinRequestsForUserAsync(id, ct));
            eeGrants.AddRange(await repo.GetEarlyEntryGrantsForUserAsync(id, ct));
        }
        var teamIds = memberships.Select(tm => tm.TeamId)
            .Concat(joinRequests.Select(tjr => tjr.TeamId))
            .Concat(eeGrants.Select(g => g.TeamId))
            .Distinct()
            .ToList();
        var teamsById = await repo.GetByIdsWithParentsAsync(teamIds, ct);

        string GetTeamName(Guid teamId) =>
            teamsById.TryGetValue(teamId, out var team) ? team.Name : string.Empty;

        var membershipSlice = new UserDataSlice(TeamMemberships, memberships.Select(tm => new
        {
            TeamName = GetTeamName(tm.TeamId),
            tm.Role,
            JoinedAt = tm.JoinedAt.ToIso8601(),
            LeftAt = tm.LeftAt.ToIso8601(),
            TeamRoles = tm.RoleAssignments.Select(tra => new
            {
                RoleName = tra.TeamRoleDefinition.Name,
                AssignedAt = tra.AssignedAt.ToIso8601()
            })
        }).ToList());

        var joinRequestSlice = new UserDataSlice(TeamJoinRequests, joinRequests.Select(tjr => new
        {
            TeamName = GetTeamName(tjr.TeamId),
            tjr.Status,
            tjr.Message,
            tjr.ReviewNotes,
            RequestedAt = tjr.RequestedAt.ToIso8601(),
            ResolvedAt = tjr.ResolvedAt.ToIso8601(),
            StateHistory = tjr.StateHistory.OrderBy(h => h.ChangedAt).Select(h => new
            {
                h.Status,
                ChangedAt = h.ChangedAt.ToIso8601(),
                h.Notes
            }).ToList()
        }).ToList());

        var earlyEntrySlice = new UserDataSlice(TeamEarlyEntry, eeGrants.Select(g => new
        {
            g.EventSettingsId,
            TeamName = GetTeamName(g.TeamId),
            g.ProjectName,
            EntryDate = g.EntryDate.ToInvariantDate(),
            GrantedAt = g.CreatedAt.ToIso8601(),
        }).ToList());

        return [membershipSlice, joinRequestSlice, earlyEntrySlice];
    }

    private ISettingsService Settings => serviceProvider.GetRequiredService<ISettingsService>();

    public async Task<IReadOnlyList<EarlyEntryGrant>> GetEarlyEntriesAsync(CancellationToken ct)
    {
        var activeEvent = Settings.GetActiveEventSettings();
        if (activeEvent is null) return [];
        var grants = await repo.GetEarlyEntryGrantsAsync(ct);
        return Project(grants.Where(g => g.EventSettingsId == activeEvent.Id).ToList());
    }

    public async Task<IReadOnlyList<TeamEarlyEntryGrantInfo>> GetEarlyEntryGrantsForTeamAsync(Guid teamId, CancellationToken ct = default)
    {
        var grants = await repo.GetEarlyEntryGrantsForTeamAsync(teamId, ct);
        return grants
            .Select(g => new TeamEarlyEntryGrantInfo(g.Id, g.UserId, g.EntryDate, g.ProjectName, g.EventSettingsId))
            .ToList();
    }

    public async Task<TeamEarlyEntryMutationResult> AddEarlyEntryGrantAsync(Guid teamId, Guid userId, LocalDate entryDate, string projectName, Guid actorUserId, Guid eventSettingsId, CancellationToken ct = default)
    {
        if (await GetTeamRefusalAsync(teamId, ct) is { } teamRefusal)
            return new(Refusal: teamRefusal);
        if (userId == Guid.Empty)
            return new(Refusal: "UserId must be a non-empty GUID.");
        var (targetUserId, membershipRefusal) = await ResolveMembershipTargetAsync(userId, ct);
        if (targetUserId is null)
            return new(Refusal: membershipRefusal);
        userId = targetUserId.Value;

        if (await GetEventRefusalAsync(eventSettingsId, ct) is { } eventRefusal)
            return new(Refusal: eventRefusal);

        var grant = new TeamEarlyEntryGrant
        {
            TeamId = teamId,
            UserId = userId,
            EventSettingsId = eventSettingsId,
            EntryDate = entryDate,
            ProjectName = projectName.Trim(),
            CreatedAt = clock.GetCurrentInstant(),
            CreatedByUserId = actorUserId,
        };
        await repo.AddEarlyEntryGrantAsync(grant, ct);
        await auditLogService.LogAsync(TeamsAuditActions.EarlyEntryGranted, nameof(TeamEarlyEntryGrant), grant.Id,
            $"EE granted to {userId} on {entryDate} for \"{grant.ProjectName}\" (team {teamId}, event {eventSettingsId})", actorUserId);
        return new();
    }

    public async Task<TeamEarlyEntryMutationResult> EditEarlyEntryGrantAsync(Guid teamId, Guid grantId, LocalDate entryDate, string projectName, Guid actorUserId, Guid eventSettingsId, CancellationToken ct = default)
    {
        var grant = await repo.FindEarlyEntryGrantForMutationAsync(grantId, ct);
        if (grant is null || grant.TeamId != teamId)
            return new(Refusal: $"EE grant {grantId} not found.");
        if (await GetTeamRefusalAsync(teamId, ct) is { } teamRefusal)
            return new(Refusal: teamRefusal);
        if (await GetEventRefusalAsync(eventSettingsId, ct) is { } eventRefusal)
            return new(Refusal: eventRefusal);
        grant.EventSettingsId = eventSettingsId;
        grant.EntryDate = entryDate;
        grant.ProjectName = projectName.Trim();
        grant.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpdateEarlyEntryGrantAsync(grant, ct);
        await auditLogService.LogAsync(TeamsAuditActions.EarlyEntryUpdated, nameof(TeamEarlyEntryGrant), grant.Id,
            $"EE updated for {grant.UserId}: {entryDate}, \"{grant.ProjectName}\", event {eventSettingsId}", actorUserId);
        return new();
    }

    public async Task<TeamEarlyEntryMutationResult> RemoveEarlyEntryGrantAsync(Guid teamId, Guid grantId, Guid actorUserId, CancellationToken ct = default)
    {
        var grant = await repo.FindEarlyEntryGrantForMutationAsync(grantId, ct);
        if (grant is null || grant.TeamId != teamId) return new(); // idempotent; another team's grant counts as missing
        if (await GetTeamRefusalAsync(teamId, ct) is { } teamRefusal)
            return new(Refusal: teamRefusal);
        await repo.RemoveEarlyEntryGrantAsync(grantId, ct);
        await auditLogService.LogAsync(TeamsAuditActions.EarlyEntryRevoked, nameof(TeamEarlyEntryGrant), grantId,
            $"EE revoked for {grant.UserId} (team {grant.TeamId})", actorUserId);
        return new();
    }

    public async Task DeleteEarlyEntryGrantsForUserAsync(Guid userId, CancellationToken ct)
    {
        var grants = await repo.GetEarlyEntryGrantsForUserAsync(userId, ct);
        if (grants.Count == 0) return;
        await repo.RemoveEarlyEntryGrantsForUserAsync(userId, ct);
    }

    private async Task<string?> GetTeamRefusalAsync(Guid teamId, CancellationToken ct)
    {
        var team = await repo.GetByIdAsync(teamId, ct);
        if (team is null)
            return $"Team {teamId} not found.";
        return team.EarlyEntryEnabled ? null : $"Early entry is not enabled for team {teamId}.";
    }

    private async Task<string?> GetEventRefusalAsync(Guid eventSettingsId, CancellationToken ct)
    {
        if (eventSettingsId == Guid.Empty
            || await Settings.GetEventSettingsByIdAsync(eventSettingsId, ct) is not { Status: not EventSettingsStatus.Deleted })
            return "Select an available event for this early-entry grant.";
        return null;
    }

    /// <summary>The caller must load each grant's <see cref="TeamEarlyEntryGrant.Team"/>.</summary>
    internal static IReadOnlyList<EarlyEntryGrant> Project(IReadOnlyList<TeamEarlyEntryGrant> grants)
    {
        var result = new List<EarlyEntryGrant>(grants.Count);
        foreach (var g in grants)
            result.Add(new EarlyEntryGrant(g.UserId, g.EntryDate, $"{g.Team.Name}: {g.ProjectName}"));
        return result;
    }

    public async Task<Dictionary<string, Guid[]>> GetExpectedAsync(
        string? groupKey = null,
        CancellationToken ct = default)
    {
        var requestedKey = string.IsNullOrWhiteSpace(groupKey)
            ? null
            : groupKey.Trim();

        var teams = await repo.GetAllActiveAsync(ct);
        var groupTeams = teams
            .Where(t => t.GoogleGroupEmail is not null && !GovernanceBodyTeamIds.Contains(t.Id))
            .Where(t => requestedKey is null
                || string.Equals(t.GoogleGroupEmail, requestedKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (groupTeams.Count == 0)
            return new Dictionary<string, Guid[]>(StringComparer.OrdinalIgnoreCase);

        var duplicateGroupEmails = groupTeams
            .GroupBy(t => t.GoogleGroupEmail!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var duplicateGroupEmail in duplicateGroupEmails)
        {
            logger.LogWarning(
                "Multiple active Teams share Google group {GroupKey}; skipping this group membership claim so Google sync fails closed",
                duplicateGroupEmail);
        }

        var syncableGroupTeams = groupTeams
            .Where(t => !duplicateGroupEmails.Contains(t.GoogleGroupEmail!))
            .ToList();
        if (syncableGroupTeams.Count == 0)
            return new Dictionary<string, Guid[]>(StringComparer.OrdinalIgnoreCase);

        var groupTeamIds = syncableGroupTeams.Select(t => t.Id).ToHashSet();
        var parentIdByChildTeamId = teams
            .Where(t => t.ParentTeamId is { } parentId && groupTeamIds.Contains(parentId))
            .ToDictionary(t => t.Id, t => t.ParentTeamId!.Value);

        var childMemberUserIdsByParentTeam = teams
            .Where(t => parentIdByChildTeamId.ContainsKey(t.Id))
            .GroupBy(t => parentIdByChildTeamId[t.Id])
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(t => t.Members.Where(m => m.LeftAt is null).Select(m => m.UserId))
                    .Distinct()
                    .ToArray());

        var result = new Dictionary<string, Guid[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var team in syncableGroupTeams)
        {
            var directUserIds = team.Members
                .Where(m => m.LeftAt is null)
                .Select(m => m.UserId);

            var childUserIds = childMemberUserIdsByParentTeam.GetValueOrDefault(team.Id)
                ?? [];

            result[team.GoogleGroupEmail!] = directUserIds
                .Concat(childUserIds)
                .Distinct()
                .ToArray();
        }

        return result;
    }

    private static readonly TrackedLock JoinRequestLock = new("Teams.JoinRequest");

    public async Task<TeamJoinResult> JoinTeamAsync(
        Guid teamId,
        Guid userId,
        string? message,
        CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdWithRelationsAsync(teamId, cancellationToken);
        if (team is null)
            return new TeamJoinResult(ErrorKey: "Teams_NotFound");

        if (team.RequiresApproval)
        {
            var result = await RequestToJoinTeamAsync(teamId, userId, message, cancellationToken);
            return result.ErrorKey is null
                ? new TeamJoinResult(TeamJoinOutcome.RequestSubmitted)
                : new TeamJoinResult(ErrorKey: result.ErrorKey);
        }

        var directResult = await JoinTeamDirectlyAsync(teamId, userId, cancellationToken);
        return directResult.ErrorKey is null
            ? new TeamJoinResult(TeamJoinOutcome.Joined)
            : new TeamJoinResult(ErrorKey: directResult.ErrorKey);
    }

    private static string? SelfJoinRefusal(Team team, bool requiresApproval)
    {
        if (!team.IsActive)
            return "Teams_NotFound";
        if (team.IsSystemTeam)
            return "Teams_Team_CannotJoinSystem";
        if (team.IsHidden)
            return "Teams_CannotJoinHidden";
        return team.RequiresApproval != requiresApproval ? "Teams_JoinPolicyChanged" : null;
    }

    public async Task<TeamJoinRequestResult> RequestToJoinTeamAsync(
        Guid teamId,
        Guid userId,
        string? message,
        CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdWithRelationsAsync(teamId, cancellationToken);
        if (team is null)
            return new TeamJoinRequestResult(ErrorKey: "Teams_NotFound");

        if (SelfJoinRefusal(team, requiresApproval: true) is { } errorKey)
            return new TeamJoinRequestResult(ErrorKey: errorKey);

        TeamJoinRequest request;
        using (await JoinRequestLock.AcquireAsync(logger, cancellationToken))
        {
            var existingRequest = await repo.FindUserPendingRequestAsync(teamId, userId, cancellationToken);
            if (existingRequest is not null)
                return new TeamJoinRequestResult(ErrorKey: "Teams_Team_AlreadyPendingRequest");

            var teamInfo = await GetTeamAsync(teamId, cancellationToken);
            var isMember = teamInfo is { IsActive: true } && teamInfo.Members.Any(m => m.UserId == userId);
            if (isMember)
                return new TeamJoinRequestResult(ErrorKey: "Teams_Team_AlreadyMember");

            request = new TeamJoinRequest
            {
                Id = Guid.NewGuid(),
                TeamId = teamId,
                UserId = userId,
                Message = message,
                RequestedAt = clock.GetCurrentInstant()
            };

            await repo.AddRequestAsync(request, cancellationToken);
        }

        // The join request is committed; finish its notices even if the browser disconnects.
        cancellationToken = CancellationToken.None;

        logger.LogInformation("User {UserId} requested to join team {TeamId}", userId, teamId);
        await SendTeamJoinRequestSubmittedNotificationAsync(team, userId, cancellationToken);

        return new TeamJoinRequestResult(request);
    }

    public async Task<TeamDirectJoinResult> JoinTeamDirectlyAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdWithRelationsAsync(teamId, cancellationToken);
        if (team is null)
            return new TeamDirectJoinResult(ErrorKey: "Teams_NotFound");

        if (SelfJoinRefusal(team, requiresApproval: false) is { } errorKey)
            return new TeamDirectJoinResult(ErrorKey: errorKey);

        var existingMember = await repo.IsActiveMemberAsync(teamId, userId, cancellationToken);
        if (existingMember)
            return new TeamDirectJoinResult(ErrorKey: "Teams_Team_AlreadyMember");

        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Role = TeamMemberRole.Member,
            JoinedAt = clock.GetCurrentInstant()
        };

        var outboxEvent = await BuildOutboxEventAsync(
            member.Id, teamId, userId, GoogleSyncOutboxEventTypes.AddUserToTeamResources, cancellationToken);

        bool success;
        try
        {
            success = await TryAddMemberWithOutboxAsync(member, outboxEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add user {UserId} to team {TeamId} directly", userId, teamId);
            throw;
        }

        if (!success)
            return new TeamDirectJoinResult(ErrorKey: "Teams_Team_AlreadyMember");

        // The mutation is committed; finish its notices even if the browser disconnects.
        cancellationToken = CancellationToken.None;

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamJoinedDirectly, nameof(Team), teamId,
            $"Joined {team.Name} directly",
            userId,
            targetId: userId, targetType: AuditSubjectTypes.User);

        await SendAddedToTeamEmailAsync(userId, team, cancellationToken);
        await SendTeamMemberAddedNotificationAsync(team, userId, cancellationToken);

        return new TeamDirectJoinResult(member);
    }

    public async Task<TeamMembershipMutationResult> WithdrawJoinRequestAsync(
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetCurrentInstant();
        var before = await repo.WithdrawRequestAsync(requestId, userId, now, cancellationToken);
        if (before != TeamJoinRequestStatus.Pending)
            return new TeamMembershipMutationResult(false, before is { } status
                ? $"Teams_Request{status}"
                : "Teams_RequestNotFound");

        logger.LogInformation("User {UserId} withdrew join request {RequestId}", userId, requestId);
        return new TeamMembershipMutationResult(true);
    }

    public async Task<TeamJoinReviewResult> ApproveJoinRequestAsync(
        Guid requestId,
        Guid approverUserId,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        var request = await repo.FindRequestWithTeamForMutationAsync(requestId, cancellationToken);
        if (request is null)
            return new(Refusal: "Join request not found");

        var canApprove = await CanUserApproveRequestsForTeamAsync(request.TeamId, approverUserId, cancellationToken);
        if (!canApprove)
            return new(Refusal: "User does not have permission to approve requests for this team");

        if (request.Status != TeamJoinRequestStatus.Pending)
            return new(Refusal: "Can only approve pending requests");
        request.Status = TeamJoinRequestStatus.Approved;
        request.ReviewedByUserId = approverUserId;
        request.ReviewNotes = notes;
        var now = clock.GetCurrentInstant();
        request.ResolvedAt = now;

        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = request.TeamId,
            UserId = request.UserId,
            Role = TeamMemberRole.Member,
            JoinedAt = now
        };

        var outboxEvent = await BuildOutboxEventAsync(
            member.Id, request.TeamId, request.UserId,
            GoogleSyncOutboxEventTypes.AddUserToTeamResources, cancellationToken);

        bool success;
        try
        {
            success = await ApproveRequestAsync(
                request, member, outboxEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to approve join request {RequestId} for user {UserId} to team {TeamId}",
                requestId, request.UserId, request.TeamId);
            throw;
        }

        if (!success)
            return new(Refusal: "User is already a member of this team");

        // The mutation is committed; finish its notices even if the browser disconnects.
        cancellationToken = CancellationToken.None;

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamJoinRequestApproved, nameof(Team), request.TeamId,
            $"Join request for {request.Team.Name} approved",
            approverUserId,
            targetId: request.UserId, targetType: AuditSubjectTypes.User);

        await SendAddedToTeamEmailAsync(request.UserId, request.Team, cancellationToken);
        await SendJoinRequestApprovedNotificationAsync(request.Team, member.UserId, cancellationToken);

        return new(Member: member);
    }

    public async Task<TeamJoinReviewResult> RejectJoinRequestAsync(
        Guid requestId,
        Guid approverUserId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            logger.LogWarning("Join request rejection refused for request {RequestId} by {ActorId}: a reason is required",
                requestId, approverUserId);
            return new(ErrorKey: "Teams_TeamAdmin_ProvideRejectionReason");
        }

        var request = await repo.FindRequestWithTeamForMutationAsync(requestId, cancellationToken);
        if (request is null)
            return new(Refusal: "Join request not found");

        var canApprove = await CanUserApproveRequestsForTeamAsync(request.TeamId, approverUserId, cancellationToken);
        if (!canApprove)
            return new(Refusal: "User does not have permission to reject requests for this team");

        var now = clock.GetCurrentInstant();
        var rejected = await repo.RejectRequestAsync(requestId, approverUserId, reason, now, cancellationToken);
        if (!rejected)
            return new(Refusal: "Can only reject pending requests");

        // The mutation is committed; finish its notices even if the browser disconnects.
        cancellationToken = CancellationToken.None;

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamJoinRequestRejected, nameof(Team), request.TeamId,
            $"Join request for team rejected: {reason}",
            approverUserId,
            targetId: request.UserId, targetType: AuditSubjectTypes.User);

        await SendJoinRequestRejectedNotificationAsync(request.Team, request.UserId, cancellationToken);
        return new();
    }

    private const int MaxTeamSlugLength = 256;
    private static readonly HashSet<string> ReservedTeamSlugs = new(StringComparer.Ordinal)
    {
        "roster", "birthdays", "map", "my", "sync", "summary", "create", "search", "tree"
    };

    // Lazy: UserService injects ITeamManagement, closing a cycle.
    private ITeamResourceService TeamResourceService
        => serviceProvider.GetRequiredService<ITeamResourceService>();

    private ISystemTeamSync SystemTeamSync
        => serviceProvider.GetRequiredService<ISystemTeamSync>();

    public async Task<Team> CreateTeamAsync(
        string name,
        string? description,
        bool requiresApproval,
        Guid? parentTeamId = null,
        string? googleGroupPrefix = null,
        bool isHidden = false,
        CancellationToken cancellationToken = default)
    {
        var result = await CreateTeamCoreAsync(name, description, requiresApproval, parentTeamId,
            googleGroupPrefix, isHidden, cancellationToken);
        result.RequireSuccess();
        return result.Team!;
    }

    internal async Task<TeamUpdateResult> CreateTeamCoreAsync(
        string name,
        string? description,
        bool requiresApproval,
        Guid? parentTeamId = null,
        string? googleGroupPrefix = null,
        bool isHidden = false,
        CancellationToken cancellationToken = default)
    {
        var baseSlug = SlugHelper.GenerateSlug(name);
        if (baseSlug.Length == 0)
            baseSlug = "team";
        var now = clock.GetCurrentInstant();

        if (ReservedTeamSlugs.Contains(baseSlug))
            return new(ErrorMessage: $"The team name '{name}' conflicts with a reserved route");

        if (parentTeamId.HasValue)
        {
            if (await ValidateParentAsync(parentTeamId.Value, cancellationToken) is { } parentError)
                return new(ErrorMessage: parentError);
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var slug = baseSlug;
            if (attempt > 0)
            {
                var suffix = "-" + (attempt + 1).ToString(CultureInfo.InvariantCulture);
                var prefixLength = Math.Min(baseSlug.Length, MaxTeamSlugLength - suffix.Length);
                slug = baseSlug[..prefixLength].TrimEnd('-') + suffix;
            }

            var collidesWithExistingSlug = await repo.SlugExistsAsync(slug, excludingTeamId: null, cancellationToken);
            if (collidesWithExistingSlug)
                continue;

            var team = new Team
            {
                Id = Guid.NewGuid(),
                Name = name,
                Description = description,
                Slug = slug,
                IsActive = true,
                RequiresApproval = requiresApproval,
                IsHidden = isHidden,
                ParentTeamId = parentTeamId,
                GoogleGroupPrefix = googleGroupPrefix,
                SystemTeamType = SystemTeamType.None,
                CreatedAt = now,
                UpdatedAt = now
            };

            var persisted = await repo.AddTeamWithRequiresApprovalOverrideAsync(team, requiresApproval, cancellationToken);
            if (persisted == TeamUpdateConflict.None)
            {
                logger.LogInformation("Created team {TeamName} with slug {Slug}", name, slug);
                return new(Team: team);
            }

            if (persisted is not (TeamUpdateConflict.SlugTaken or TeamUpdateConflict.CustomSlugTaken))
                return new(Conflict: persisted);

            // Slug unique-constraint race: retry with the next suffix.
            logger.LogDebug(
                "Slug collision for '{Slug}' at persist, retrying (attempt {Attempt})",
                slug, attempt + 1);
        }

        return new(ErrorMessage: $"Could not generate unique slug for team '{name}' after 10 attempts");
    }

    private async Task<string?> ValidateParentAsync(Guid parentTeamId, CancellationToken ct)
    {
        var parent = await repo.GetByIdAsync(parentTeamId, ct);
        if (parent is null) return $"Parent team {parentTeamId} not found";
        if (parent.IsSystemTeam) return "System teams cannot be parents";
        return parent.ParentTeamId.HasValue
            ? "Cannot nest more than one level — the parent team already has a parent"
            : null;
    }

    public async Task<TeamUpdateResult> UpdateTeamAsync(
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
        CancellationToken cancellationToken = default)
    {
        var team = await repo.FindForMutationAsync(teamId, cancellationToken);
        if (team is null) return new(ErrorMessage: $"Team {teamId} not found");

        if (team.IsSystemTeam)
        {
            team.Description = description;
            team.GoogleGroupPrefix = googleGroupPrefix;
            team.UpdatedAt = clock.GetCurrentInstant();
            var conflict = await repo.UpdateTeamAsync(team, cancellationToken);
            return conflict == TeamUpdateConflict.None ? new(Team: team) : new(Conflict: conflict);
        }

        if (parentTeamId.HasValue)
        {
            if (parentTeamId.Value == teamId)
                return new(ErrorMessage: "A team cannot be its own parent");

            var hasChildren = await repo.HasActiveChildrenAsync(teamId, cancellationToken);
            if (hasChildren)
                return new(ErrorMessage: "This team has sub-teams and cannot become a child of another team");

            if (await ValidateParentAsync(parentTeamId.Value, cancellationToken) is { } parentError)
                return new(ErrorMessage: parentError);
        }

        if (!string.IsNullOrWhiteSpace(customSlug))
        {
            var normalized = SlugHelper.GenerateSlug(customSlug);
            if (string.IsNullOrEmpty(normalized))
                return new(ErrorMessage: "Custom slug is not valid. Use lowercase letters, numbers, and hyphens.");
            if (ReservedTeamSlugs.Contains(normalized))
                return new(ErrorMessage: $"The slug '{normalized}' conflicts with a reserved route.");

            var customSlugTaken = await repo.SlugExistsAsync(normalized, excludingTeamId: teamId, cancellationToken);
            if (customSlugTaken)
                return new(ErrorMessage: $"The slug '{normalized}' is already in use by another team.");

            customSlug = normalized;
        }
        else
        {
            customSlug = null;
        }

        var wasActiveDepartment = team.IsActive && !team.ParentTeamId.HasValue;
        var willBeActiveDepartment = isActive && !parentTeamId.HasValue;
        var coordinatorUserIds = wasActiveDepartment != willBeActiveDepartment
            ? await repo.GetCoordinatorUserIdsAsync(teamId, cancellationToken)
            : (IReadOnlyList<Guid>)[];

        var nameChanged = !string.Equals(team.Name, name, StringComparison.Ordinal);
        if (nameChanged)
        {
            var newSlug = SlugHelper.GenerateSlug(name);
            if (!string.IsNullOrEmpty(newSlug) && !ReservedTeamSlugs.Contains(newSlug)
                && !await repo.SlugExistsAsync(newSlug, excludingTeamId: teamId, cancellationToken))
                team.Slug = newSlug;
        }

        team.Name = name;
        team.Description = description;
        team.RequiresApproval = requiresApproval;
        team.IsActive = isActive;
        team.ParentTeamId = parentTeamId;
        team.GoogleGroupPrefix = googleGroupPrefix;
        team.CustomSlug = customSlug;
        if (hasBudget.HasValue)
            team.HasBudget = hasBudget.Value;
        if (isHidden.HasValue)
            team.IsHidden = isHidden.Value;
        if (isSensitive.HasValue && isSensitive.Value != team.IsSensitive)
        {
            // Only a global Admin writes IsSensitive, enforced here so every caller is held to it. Gated on the change,
            // not the argument: a caller passing the current value (the seeders do) isn't writing.
            await adminAuthorization.RequireCurrentUserIsAdminAsync(cancellationToken);
            team.IsSensitive = isSensitive.Value;
        }
        if (isPromotedToDirectory.HasValue)
            team.IsPromotedToDirectory = isPromotedToDirectory.Value;
        if (earlyEntryEnabled is { } eeFlag)
            team.EarlyEntryEnabled = eeFlag;
        if (team.IsSystemTeam || parentTeamId.HasValue)
        {
            team.IsPublicPage = false;
            team.ShowCoordinatorsOnPublicPage = false;
        }
        team.UpdatedAt = clock.GetCurrentInstant();

        var updateConflict = await repo.UpdateTeamAsync(team, cancellationToken);
        if (updateConflict != TeamUpdateConflict.None) return new(Conflict: updateConflict);

        foreach (var userId in coordinatorUserIds)
            await SystemTeamSync.SyncMembershipForUserAsync(userId, SystemTeamType.Coordinators, cancellationToken);

        logger.LogInformation("Updated team {TeamId} ({TeamName})", teamId, name);

        return new(Team: team);
    }

    public async Task<TeamDeactivationResult> DeleteTeamAsync(Guid teamId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdAsync(teamId, cancellationToken);
        if (team is null)
            return new(Refusal: $"Team {teamId} not found");

        if (team.IsSystemTeam)
            return new(Refusal: "Cannot delete system team");

        var hasActiveChildren = await repo.HasActiveChildrenAsync(teamId, cancellationToken);
        if (hasActiveChildren)
            return new(Refusal: "Cannot deactivate a team that has active sub-teams. Remove or reassign sub-teams first.");

        var now = clock.GetCurrentInstant();

        var coordinatorUserIds = team.ParentTeamId.HasValue
            ? (IReadOnlyList<Guid>)[]
            : await repo.GetCoordinatorUserIdsAsync(teamId, cancellationToken);
        var closedUsers = await repo.DeactivateTeamAsync(teamId, now, cancellationToken);
        foreach (var userId in closedUsers)
        {
            await RoleWrite.RemoveAsync(TeamOwner.Of(teamId), userId, actorUserId, cancellationToken);
            await auditLogService.LogAsync(
                TeamsAuditActions.TeamMemberRemoved, nameof(Team), teamId,
                $"Membership ended when {team.Name} was deactivated",
                actorUserId, targetId: userId, targetType: AuditSubjectTypes.User);
        }

        foreach (var userId in coordinatorUserIds)
            await SystemTeamSync.SyncMembershipForUserAsync(userId, SystemTeamType.Coordinators, cancellationToken);

        logger.LogInformation(
            "Deactivated team {TeamId} ({TeamName}); closed {MemberCount} memberships",
            teamId, team.Name, closedUsers.Count);
        return new();
    }

    public async Task<int> RetireGovernanceBodyTeamsAsync(Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var retired = 0;
        foreach (var teamId in GovernanceBodyTeamIds)
        {
            if (await repo.GetByIdAsync(teamId, cancellationToken) is not { IsActive: true } team)
                continue;

            var closedUsers = await repo.DeactivateTeamAsync(teamId, clock.GetCurrentInstant(), cancellationToken);
            foreach (var userId in closedUsers)
            {
                await auditLogService.LogAsync(
                    TeamsAuditActions.TeamMemberRemoved, nameof(Team), teamId,
                    $"Membership ended when {team.Name} was deactivated",
                    actorUserId, targetId: userId, targetType: AuditSubjectTypes.User);
            }

            logger.LogInformation(
                "Retired governance body team {TeamId} ({TeamName}); closed {MemberCount} memberships",
                teamId, team.Name, closedUsers.Count);
            retired++;
        }

        return retired;
    }

    public async Task<int> RemoveGovernanceBodyRolesAsync(Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var removed = 0;
        foreach (var teamId in GovernanceBodyTeamIds)
        {
            if (await repo.GetByIdAsync(teamId, cancellationToken) is not { } team)
                continue;

            foreach (var listed in await repo.GetRoleDefinitionsAsync(teamId, cancellationToken))
            {
                if (await repo.FindRoleDefinitionForMutationAsync(listed.Id, cancellationToken) is not { } definition)
                    continue;

                var holders = definition.Assignments.Select(a => a.TeamMember.UserId).Distinct().ToList();
                await repo.RemoveRoleDefinitionAsync(definition, cancellationToken);

                foreach (var userId in holders)
                {
                    await auditLogService.LogAsync(
                        TeamsAuditActions.TeamRoleUnassigned, nameof(TeamRoleDefinition), definition.Id,
                        $"Unassigned from role '{definition.Name}' in {team.Name} before the team was deleted",
                        actorUserId, targetId: userId, targetType: AuditSubjectTypes.User);
                }

                await auditLogService.LogAsync(
                    TeamsAuditActions.TeamRoleDefinitionDeleted, nameof(TeamRoleDefinition), definition.Id,
                    $"Role definition '{definition.Name}' deleted from team {team.Name} before the team was deleted",
                    actorUserId, targetId: teamId, targetType: nameof(Team));

                if (definition.IsManagement)
                {
                    foreach (var userId in holders)
                        await SystemTeamSync.SyncMembershipForUserAsync(userId, SystemTeamType.Coordinators, cancellationToken);
                }

                removed++;
            }
        }

        return removed;
    }

    public async Task<TeamMembershipMutationResult> LeaveTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdAsync(teamId, cancellationToken);
        if (team is null)
            return new TeamMembershipMutationResult(false, "Teams_NotFound");

        if (team.IsSystemTeam)
            return new TeamMembershipMutationResult(false, "Teams_CannotLeaveSystem");

        var member = await repo.FindActiveMemberForMutationAsync(teamId, userId, cancellationToken);
        if (member is null)
            return new TeamMembershipMutationResult(false, "Teams_NotMember");

        var wasCoordinator = member.Role == TeamMemberRole.Coordinator;

        await RoleWrite.RemoveAsync(TeamOwner.Of(teamId), userId, userId, cancellationToken);
        await MarkMemberLeftWithOutboxAsync(member, null, cancellationToken);

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamLeft, nameof(Team), teamId,
            $"Left {team.Name}",
            userId,
            targetId: userId, targetType: AuditSubjectTypes.User);

        if (wasCoordinator)
        {
            await SystemTeamSync.SyncMembershipForUserAsync(
                userId, SystemTeamType.Coordinators, cancellationToken);
        }

        return new TeamMembershipMutationResult(true, WasCoordinator: wasCoordinator);
    }

    public Task<TeamMemberRemovalResult> RemoveMemberAsync(
        Guid teamId,
        Guid userId,
        Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        RemoveMemberCoreAsync(teamId, userId, actorUserId, null, cancellationToken);

    private async Task<TeamMemberRemovalResult> RemoveMemberCoreAsync(
        Guid teamId, Guid userId, Guid actorUserId, Guid? mergeTargetUserId, CancellationToken cancellationToken)
    {
        var team = await repo.GetByIdAsync(teamId, cancellationToken);
        if (team is null)
            return RefuseMemberRemoval(teamId, userId, actorUserId, "Teams_RemoveMember_TeamMissing", $"Team {teamId} not found");

        if (team.IsSystemTeam)
            return RefuseMemberRemoval(teamId, userId, actorUserId, "Teams_RemoveMember_SystemTeam", "Cannot remove members from system team manually");

        var canApprove = await CanUserApproveRequestsForTeamAsync(teamId, actorUserId, cancellationToken);
        if (!canApprove)
            return RefuseMemberRemoval(teamId, userId, actorUserId, "Teams_RemoveMember_Permission", "User does not have permission to remove members from this team");

        var member = await repo.FindActiveMemberForMutationAsync(teamId, userId, cancellationToken);
        if (member is null)
            return RefuseMemberRemoval(teamId, userId, actorUserId, "Teams_RemoveMember_NotMember", "User is not a member of this team");

        TeamMember? mergeTarget = null;
        if (mergeTargetUserId is { } targetUserId)
        {
            if (targetUserId == userId)
                throw new InvalidOperationException("Cannot merge a membership into itself");
            mergeTarget = await repo.FindActiveMemberForMutationAsync(teamId, targetUserId, cancellationToken);
            if (mergeTarget is null)
            {
                var addition = await AddMemberToTeamAsync(teamId, targetUserId, actorUserId, cancellationToken);
                mergeTarget = addition.Succeeded ? addition.Member! : throw new InvalidOperationException(addition.Refusal);
            }
        }
        var wasCoordinator = member.Role == TeamMemberRole.Coordinator;

        // A merge leaves Roles alone: Roles' own merge step moves the person's roles to the survivor.
        if (mergeTarget is null)
            await RoleWrite.RemoveAsync(TeamOwner.Of(teamId), userId, actorUserId, cancellationToken);
        var removedAssignments = await MarkMemberLeftWithOutboxAsync(member, mergeTarget?.Id, cancellationToken);

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamMemberRemoved, nameof(Team), teamId,
            $"Member removed from {team.Name}",
            actorUserId,
            targetId: userId, targetType: AuditSubjectTypes.User);

        if (mergeTarget is not null && (wasCoordinator || removedAssignments.Count > 0))
        {
            await auditLogService.LogAsync(
                TeamsAuditActions.TeamMemberRoleChanged, nameof(Team), teamId,
                $"Folded {removedAssignments.Count} role assignment(s) from merged account {userId}; source coordinator: {wasCoordinator}",
                actorUserId, mergeTarget.UserId, AuditSubjectTypes.User);
        }

        await SendMemberRemovedNotificationAsync(team, userId, teamId, cancellationToken);

        // A removed coordinator's Coordinators eligibility is reconciled in the mutation, not left to the caller.
        if (wasCoordinator && mergeTarget is null)
            await SystemTeamSync.SyncMembershipForUserAsync(userId, SystemTeamType.Coordinators, cancellationToken);
        return new TeamMemberRemovalResult();
    }

    private TeamMemberRemovalResult RefuseMemberRemoval(Guid teamId, Guid userId, Guid actorId, string key, string message)
    {
        logger.LogWarning("Member removal refused for {UserId} from team {TeamId} by {ActorId}: {ErrorKey}", userId, teamId, actorId, key);
        return new TeamMemberRemovalResult(key, message);
    }

    public async Task<TeamJoinReviewResult> AddMemberToTeamAsync(
        Guid teamId,
        Guid targetUserId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdAsync(teamId, cancellationToken);
        if (team is null)
            return new(Refusal: $"Team {teamId} not found");

        if (team.IsSystemTeam)
            return new(Refusal: "Cannot add members to system teams manually");

        var (resolvedUserId, membershipRefusal) = await ResolveMembershipTargetAsync(targetUserId, cancellationToken);
        if (resolvedUserId is null)
            return new(Refusal: membershipRefusal);
        targetUserId = resolvedUserId.Value;
        var isExisting = await repo.IsActiveMemberAsync(teamId, targetUserId, cancellationToken);
        if (isExisting)
            return new(Refusal: "User is already a member of this team");

        // A pending request is resolved by approving it with the membership.
        var pendingRequest = await repo.FindUserPendingRequestAsync(teamId, targetUserId, cancellationToken);
        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = targetUserId,
            Role = TeamMemberRole.Member,
            JoinedAt = clock.GetCurrentInstant()
        };

        var outboxEvent = await BuildOutboxEventAsync(
            member.Id, teamId, targetUserId, GoogleSyncOutboxEventTypes.AddUserToTeamResources, cancellationToken);

        bool success;
        try
        {
            if (pendingRequest is not null)
            {
                var tracked = await repo.FindRequestForMutationAsync(pendingRequest.Id, cancellationToken);
                if (tracked is not null)
                {
                    if (tracked.Status == TeamJoinRequestStatus.Pending)
                    {
                        tracked.Status = TeamJoinRequestStatus.Approved;
                        tracked.ReviewedByUserId = actorUserId;
                        tracked.ReviewNotes = "Added directly by team manager";
                        tracked.ResolvedAt = clock.GetCurrentInstant();
                    }
                    success = await ApproveRequestAsync(
                        tracked, member, outboxEvent, cancellationToken);
                }
                else
                {
                    success = await TryAddMemberWithOutboxAsync(member, outboxEvent, cancellationToken);
                }
            }
            else
            {
                success = await TryAddMemberWithOutboxAsync(member, outboxEvent, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add user {UserId} to team {TeamId}", targetUserId, teamId);
            throw;
        }

        if (!success)
            return new(Refusal: "User is already a member of this team");

        // The mutation is committed; finish its notices even if the browser disconnects.
        cancellationToken = CancellationToken.None;

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamMemberAdded, nameof(Team), teamId,
            $"Member added to {team.Name}",
            actorUserId,
            targetId: targetUserId, targetType: AuditSubjectTypes.User);

        await SendAddedToTeamEmailAsync(targetUserId, team, cancellationToken);

        return new(Member: member);
    }

    public async Task SetMemberRoleAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var newRole = await repo.SetMemberRoleAsync(teamId, userId, role, cancellationToken);
        if (newRole is null)
            return;

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamMemberRoleChanged, nameof(Team), teamId,
            $"Set member role to {role}",
            actorUserId,
            targetId: userId, targetType: AuditSubjectTypes.User);
    }

    public async Task<int> RevokeAllMembershipsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        foreach (var membership in await GetUserTeamMembershipsAsync(userId, cancellationToken))
            await RoleWrite.RemoveAsync(TeamOwner.Of(membership.TeamId), userId, userId, cancellationToken);

        return await repo.RevokeAllMembershipsAsync(userId, clock.GetCurrentInstant(), cancellationToken);
    }

    public async Task ReassignAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Guid actorUserId,
        Instant updatedAt,
        CancellationToken cancellationToken)
    {
        // Ends source memberships through the ordinary removal/outbox path, folding roles into the survivor in the
        // same save. System teams reconcile separately.
        var sourceMemberships = await GetUserTeamMembershipsAsync(sourceUserId, cancellationToken);
        var sourceTeamsById = await repo.GetByIdsWithParentsAsync(
            sourceMemberships.Select(m => m.TeamId).Distinct().ToList(), cancellationToken);

        foreach (var membership in sourceMemberships)
        {
            if (sourceTeamsById.TryGetValue(membership.TeamId, out var team) && team.IsSystemTeam)
                continue;

            var removal = await RemoveMemberCoreAsync(
                membership.TeamId, sourceUserId, actorUserId, targetUserId, cancellationToken);
            if (removal.ErrorKey is not null)
                throw new InvalidOperationException(removal.ErrorMessage);
        }

        await repo.ReassignActiveJoinRequestsAsync(sourceUserId, targetUserId, cancellationToken);

        await repo.ReassignEarlyEntryGrantsAsync(sourceUserId, targetUserId, cancellationToken);
        // Always retry both reconciles, even when an earlier pass ended every source membership before failing here.
        await SystemTeamSync.SyncMembershipForUserAsync(sourceUserId, SystemTeamType.Coordinators, cancellationToken);
        await SystemTeamSync.SyncMembershipForUserAsync(targetUserId, SystemTeamType.Coordinators, cancellationToken);
    }

    private static readonly ResourceManager NoticeResources = new(typeof(TeamsResource));

    // Resolve lazily: Users and Google resources can depend on Teams.
    private IEmailService EmailService => serviceProvider.GetRequiredService<IEmailService>();
    private TeamsEmails EmailMessages => serviceProvider.GetRequiredService<TeamsEmails>();

    public Task SendTeamJoinRequestSubmittedNotificationAsync(
        Team team, Guid userId, CancellationToken cancellationToken) =>
        SendCoordinatorNoticeAsync(team, userId, joinRequest: true, cancellationToken);

    public Task SendTeamMemberAddedNotificationAsync(
        Team team, Guid userId, CancellationToken cancellationToken) =>
        SendCoordinatorNoticeAsync(team, userId, joinRequest: false, cancellationToken);

    private async Task SendCoordinatorNoticeAsync(
        Team team, Guid userId, bool joinRequest, CancellationToken cancellationToken)
    {
        var coordinatorUserIds = GetCoordinatorUserIds(team);
        if (coordinatorUserIds.Count == 0) return;
        var source = joinRequest
            ? TeamsNotificationSources.TeamJoinRequestSubmitted
            : TeamsNotificationSources.TeamMemberAdded;
        try
        {
            var displayName = (await UserService.GetUserInfoAsync(userId, cancellationToken))?.BurnerName;
            var recipientsByCulture = new Dictionary<CultureInfo, List<Guid>>();
            var recipientIds = new HashSet<Guid>();
            foreach (var coordinatorId in coordinatorUserIds)
            {
                var (recipientId, culture) = await ResolveRecipientAsync(coordinatorId, cancellationToken);
                if (!recipientIds.Add(recipientId)) continue;
                if (!recipientsByCulture.TryGetValue(culture, out var recipients))
                    recipientsByCulture[culture] = recipients = [];
                recipients.Add(recipientId);
            }

            foreach (var (culture, recipients) in recipientsByCulture)
            {
                try
                {
                    var name = displayName ?? NoticeResources.GetString("Teams_Notification_Someone", culture)!;
                    var title = joinRequest
                        ? string.Format(culture, NoticeResources.GetString("Teams_Notification_JoinRequestSubmitted", culture)!, team.Name)
                        : string.Format(culture, NoticeResources.GetString("Teams_Notification_MemberJoined", culture)!, name, team.Name);
                    var bodyKey = joinRequest ? "Teams_Notification_JoinRequestSubmittedBody" : "Teams_Notification_MemberJoinedBody";
                    var actionKey = joinRequest ? "Teams_Notification_ReviewRequest" : "Teams_Notification_ViewMembers";
                    await notificationService.SendAsync(
                        source,
                        joinRequest ? NotificationClass.Actionable : NotificationClass.Informational,
                        NotificationPriority.Normal,
                        title,
                        recipients,
                        body: string.Format(culture, NoticeResources.GetString(bodyKey, culture)!, name, team.Name),
                        actionUrl: $"/Teams/{team.Slug}/Members",
                        actionLabel: NoticeResources.GetString(actionKey, culture),
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to dispatch {Source} notification for team {TeamId} in {Culture}",
                        source, team.Id, culture.Name);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to prepare {Source} notification for team {TeamId}", source, team.Id);
        }
    }

    private static IReadOnlyList<Guid> GetCoordinatorUserIds(Team team) =>
        team.Members
            .Where(member => member.Role == TeamMemberRole.Coordinator && member.LeftAt is null)
            .Select(member => member.UserId)
            .Distinct()
            .ToList();

    private async Task<(Guid UserId, CultureInfo Culture)> ResolveRecipientAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var person = await UserService.GetUserInfoAsync(userId, cancellationToken);
            var language = person?.PreferredLanguage;
            return (person?.Id ?? userId, CultureInfo.GetCultureInfo(language.IsSupportedCultureCode() ? language! : "en"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve notification language for user {UserId}; using English", userId);
            return (userId, CultureInfo.GetCultureInfo("en"));
        }
    }

    public async Task SendJoinRequestApprovedNotificationAsync(
        Team team,
        Guid requesterUserId,
        CancellationToken cancellationToken)
    {
        try
        {
            var (recipientId, culture) = await ResolveRecipientAsync(requesterUserId, cancellationToken);
            var title = string.Format(culture, NoticeResources.GetString("Teams_Notification_JoinApproved", culture)!, team.Name);
            await notificationService.SendAsync(
                TeamsNotificationSources.TeamJoinRequestDecided,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                title,
                [recipientId],
                body: string.Format(culture, NoticeResources.GetString("Teams_Notification_Welcome", culture)!, team.Name),
                actionUrl: $"/Teams/{team.Slug}",
                actionLabel: NoticeResources.GetString("Teams_Notification_ViewTeam", culture),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch TeamJoinRequestDecided notification for team {TeamId}", team.Id);
        }
    }

    public async Task SendJoinRequestRejectedNotificationAsync(
        Team team,
        Guid requesterUserId,
        CancellationToken cancellationToken)
    {
        try
        {
            var (recipientId, culture) = await ResolveRecipientAsync(requesterUserId, cancellationToken);
            var title = string.Format(culture, NoticeResources.GetString("Teams_Notification_JoinRejected", culture)!, team.Name);
            await notificationService.SendAsync(
                TeamsNotificationSources.TeamJoinRequestDecided,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                title,
                [recipientId],
                body: string.Format(culture, NoticeResources.GetString("Teams_Notification_JoinRejected", culture)!, team.Name) + ".",
                actionUrl: "/Teams",
                actionLabel: NoticeResources.GetString("Teams_MyTeams_BrowseTeams", culture),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch TeamJoinRequestDecided notification for team {TeamId}", team.Id);
        }
    }

    public async Task SendAddedToTeamEmailAsync(Guid userId, Team team, CancellationToken cancellationToken)
    {
        if (team.IsHidden) return;

        var recipientId = userId;
        var culture = CultureInfo.GetCultureInfo("en");
        try
        {
            var users = await UserService.GetUserInfosAsync([userId], cancellationToken);
            if (!users.TryGetValue(userId, out var user))
                return;
            if (user.IsTombstone)
            {
                logger.LogWarning("Skipping added-to-team welcome for unavailable user {UserId} team {TeamId}", userId, team.Id);
                return;
            }

            recipientId = user.Id;
            culture = CultureInfo.GetCultureInfo(user.PreferredLanguage.IsSupportedCultureCode() ? user.PreferredLanguage : "en");

            var email = user.Email;
            if (string.IsNullOrWhiteSpace(email))
            {
                logger.LogWarning(
                    "Skipping added-to-team email for user {UserId}: no notification-target email",
                    userId);
            }
            else
            {
                var resources = await TeamResourceService.GetTeamResourcesAsync(team.Id, cancellationToken);

                await EmailService.SendAsync(EmailMessages.AddedToTeam(
                    user.Id,
                    email, user.BurnerName, team.Name, team.Slug,
                    resources.Select(r => (r.Name, r.Url)),
                    culture.Name),
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send added-to-team email for user {UserId} team {TeamId}", userId, team.Id);
        }

        try
        {
            var title = string.Format(culture, NoticeResources.GetString("Teams_Email_AddedToTeam_Subject", culture)!, team.Name);
            await notificationService.SendAsync(
                TeamsNotificationSources.TeamMemberAdded,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                title,
                [recipientId],
                body: null,
                actionUrl: $"/Teams/{team.Slug}",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch added-to-team inbox notification for user {UserId} team {TeamId}", userId, team.Id);
        }
    }

    public async Task SendMemberRemovedNotificationAsync(
        Team team, Guid userId, Guid teamId, CancellationToken cancellationToken)
    {
        try
        {
            var (recipientId, culture) = await ResolveRecipientAsync(userId, cancellationToken);
            var title = string.Format(culture, NoticeResources.GetString("Teams_Notification_MemberRemoved", culture)!, team.Name);
            await notificationService.SendAsync(
                TeamsNotificationSources.TeamMemberRemoved,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                title,
                [recipientId],
                body: null,
                actionUrl: "/Teams",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch TeamMemberRemoved notification for user {UserId} team {TeamId}", userId, teamId);
        }
    }

    // Lazy: Google's identity read resolves Users, which depends on Teams.
    private IGoogleService GoogleIdentities
        => serviceProvider.GetRequiredService<IGoogleService>();

    internal async Task<bool> TryAddMemberWithOutboxAsync(
        TeamMember member,
        GoogleSyncOutboxEvent? outboxEvent,
        CancellationToken ct)
    {
        if (outboxEvent is null)
            return await repo.TryAddMemberAsync(member, ct);

        using var scope = CreateTransactionScope();
        var success = await repo.TryAddMemberAsync(member, ct);
        if (!success)
            return false;

        await GoogleSyncOutboxService.AddAsync(outboxEvent, ct);
        scope.Complete();
        return true;
    }

    internal async Task<bool> ApproveRequestAsync(
        TeamJoinRequest request,
        TeamMember member,
        GoogleSyncOutboxEvent? outboxEvent,
        CancellationToken ct)
    {
        if (outboxEvent is null)
            return await repo.ApproveRequestWithMemberAsync(request, member, ct);

        using var scope = CreateTransactionScope();
        var success = await repo.ApproveRequestWithMemberAsync(request, member, ct);
        if (!success)
            return false;

        await GoogleSyncOutboxService.AddAsync(outboxEvent, ct);
        scope.Complete();
        return true;
    }

    internal async Task<IReadOnlyList<TeamRoleAssignment>> MarkMemberLeftWithOutboxAsync(
        TeamMember member,
        Guid? mergeIntoMemberId,
        CancellationToken ct)
    {
        var outboxEvent = await BuildOutboxEventAsync(
            member.Id, member.TeamId, member.UserId, GoogleSyncOutboxEventTypes.RemoveUserFromTeamResources, ct);
        var leftAt = clock.GetCurrentInstant();
        if (outboxEvent is null)
            return await repo.MarkMemberLeftAsync(member.Id, leftAt, mergeIntoMemberId, ct);

        using var scope = CreateTransactionScope();
        var removedAssignments = await repo.MarkMemberLeftAsync(member.Id, leftAt, mergeIntoMemberId, ct);
        await GoogleSyncOutboxService.AddAsync(outboxEvent, ct);
        scope.Complete();
        return removedAssignments;
    }

    internal async Task<TeamRoleAssignmentMutationResult> AssignToRoleWithOutboxAsync(
        Guid roleDefinitionId,
        Guid targetUserId,
        Guid actorUserId,
        TeamMember? autoAddMember,
        GoogleSyncOutboxEvent? outboxEvent,
        bool promoteToCoordinator,
        Instant now,
        CancellationToken ct)
    {
        if (outboxEvent is null)
        {
            return await repo.AssignToRoleAsync(
                roleDefinitionId,
                targetUserId,
                actorUserId,
                autoAddMember,
                promoteToCoordinator,
                now,
                ct);
        }

        using var scope = CreateTransactionScope();
        var result = await repo.AssignToRoleAsync(
            roleDefinitionId,
            targetUserId,
            actorUserId,
            autoAddMember,
            promoteToCoordinator,
            now,
            ct);

        if (!result.Succeeded)
            return result;
        if (autoAddMember is not null)
            await GoogleSyncOutboxService.AddAsync(outboxEvent, ct);

        scope.Complete();
        return result;
    }

    private static TransactionScope CreateTransactionScope() =>
        new(
            TransactionScopeOption.Required,
            new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
            TransactionScopeAsyncFlowOption.Enabled);

    internal async Task<GoogleSyncOutboxEvent?> BuildOutboxEventAsync(
        Guid teamMemberId,
        Guid teamId,
        Guid userId,
        string eventType,
        CancellationToken ct)
    {
        var identity = await GoogleIdentities.GetWorkspaceIdentityAsync(userId, ct);
        if (identity?.Status == GoogleEmailStatus.Rejected)
        {
            logger.LogDebug(
                "Skipping Google sync outbox event {EventType} for user {UserId} — Google identity is Rejected",
                eventType, userId);
            return null;
        }

        return new GoogleSyncOutboxEvent
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            TeamId = teamId,
            UserId = userId,
            OccurredAt = clock.GetCurrentInstant(),
            DeduplicationKey = $"{teamMemberId}:{eventType}"
        };
    }

    public async Task<TeamPageUpdateResult> UpdateTeamPageContentAsync(
        Guid teamId,
        string? pageContent,
        IReadOnlyList<TeamPageCallToActionInput> callsToAction,
        bool isPublicPage,
        bool showCoordinatorsOnPublicPage,
        Guid updatedByUserId,
        CancellationToken cancellationToken = default)
    {
        var normalizedCallsToAction = callsToAction
            .Where(c => !string.IsNullOrWhiteSpace(c.Text) && !string.IsNullOrWhiteSpace(c.Url))
            .Select(c => new CallToAction { Text = c.Text!.Trim(), Url = c.Url!.Trim(), Style = c.Style })
            .ToList();

        try
        {
            var team = await repo.FindForMutationAsync(teamId, cancellationToken);
            if (team is null)
                return Refuse($"Team {teamId} not found");

            if (normalizedCallsToAction.Count > 3)
                return Refuse("A team can have at most 3 calls to action.");

            if (normalizedCallsToAction.Count(c => c.Style == CallToActionStyle.Primary) > 1)
                return Refuse("Only one primary call to action is allowed.");

            if (normalizedCallsToAction.Any(c => !IsSafeCallToActionUrl(c.Url)))
                return Refuse("Calls to action require a relative URL or an HTTP, HTTPS or mailto URL.");

            var canBePublic = !team.IsSystemTeam && !team.ParentTeamId.HasValue;

            if (isPublicPage && !canBePublic)
                return Refuse("Only departments (non-system, top-level teams) can be made public.");

            var now = clock.GetCurrentInstant();
            team.PageContent = pageContent;
            team.CallsToAction = normalizedCallsToAction;
            team.IsPublicPage = isPublicPage;
            team.ShowCoordinatorsOnPublicPage = canBePublic && isPublicPage && showCoordinatorsOnPublicPage;
            team.PageContentUpdatedAt = now;
            team.PageContentUpdatedByUserId = updatedByUserId;
            team.UpdatedAt = now;

            var conflict = await repo.UpdateTeamAsync(team, cancellationToken);
            if (conflict != TeamUpdateConflict.None)
            {
                logger.LogWarning("Team page update rejected for team {TeamId}: {Conflict}", teamId, conflict);
                return new TeamPageUpdateResult(false, null);
            }

            await auditLogService.LogAsync(
                TeamsAuditActions.TeamPageContentUpdated, nameof(Team), teamId,
                $"Team page content updated. Public: {isPublicPage}",
                updatedByUserId);

            return TeamPageUpdateResult.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to update team page for team {TeamId} by user {UserId}", teamId, updatedByUserId);
            return new TeamPageUpdateResult(false, null);
        }

        TeamPageUpdateResult Refuse(string reason)
        {
            logger.LogWarning("Team page update rejected for team {TeamId} by user {UserId}: {Reason}", teamId, updatedByUserId, reason);
            return TeamPageUpdateResult.Failed(reason);
        }
    }

    // Shared by writes and the page projection, so unsafe legacy values never become links.
    internal static bool IsSafeCallToActionUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && !url.Any(char.IsControl)
        && Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri)
        && (!uri.IsAbsoluteUri || uri.Scheme is "http" or "https" or "mailto");

    public async Task<TeamRoleDefinitionMutationResult> CreateRoleDefinitionAsync(
        Guid teamId, string name, string? description, int slotCount,
        List<SlotPriority> priorities, int sortOrder, RolePeriod period, Guid actorUserId,
        bool isPublic = true,
        int? estimatedHours = null,
        CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdAsync(teamId, cancellationToken);
        if (team is null)
            return new(Refusal: $"Team {teamId} not found");

        if (RoleCapacityRefusal(slotCount, priorities, estimatedHours) is { } prioritiesRefusal)
            return new(Refusal: prioritiesRefusal);
        if (RoleNameRefusal(name) is { } nameRefusal)
            return new(Refusal: nameRefusal);

        var lowerName = name.ToLowerInvariant();
        var nameExists = await repo.RoleDefinitionNameExistsAsync(teamId, lowerName, excludingId: null, cancellationToken);
        if (nameExists)
            return new(Refusal: $"A role definition with name '{name}' already exists for this team");

        var canManage = await CanUserApproveRequestsForTeamAsync(teamId, actorUserId, cancellationToken);
        if (!canManage)
            return new(Refusal: "User does not have permission to manage role definitions for this team");

        var now = clock.GetCurrentInstant();
        var definition = new TeamRoleDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Name = name,
            Description = description,
            SlotCount = slotCount,
            EstimatedHours = estimatedHours,
            Priorities = priorities,
            SortOrder = sortOrder,
            IsPublic = isPublic,
            Period = period,
            CreatedAt = now,
            UpdatedAt = now
        };

        await repo.AddRoleDefinitionAsync(definition, cancellationToken);

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamRoleDefinitionCreated, nameof(TeamRoleDefinition), definition.Id,
            $"Role definition '{name}' created for team {team.Name}",
            actorUserId,
            targetId: teamId, targetType: nameof(Team));

        return new(Definition: definition);
    }

    public async Task<TeamRoleDefinitionMutationResult> UpdateRoleDefinitionAsync(
        Guid roleDefinitionId, string name, string? description, int slotCount,
        List<SlotPriority> priorities, int sortOrder, bool? isManagement, RolePeriod period, Guid actorUserId,
        bool isPublic = true,
        int? estimatedHours = null,
        CancellationToken cancellationToken = default)
    {
        var definition = await repo.FindRoleDefinitionForMutationAsync(roleDefinitionId, cancellationToken);
        if (definition is null)
            return new(Refusal: $"Role definition {roleDefinitionId} not found");
        var team = await GetRoleDefinitionTeamAsync(definition, cancellationToken);

        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            return new(Refusal: "User does not have permission to manage role definitions for this team");

        if (RoleCapacityRefusal(slotCount, priorities, estimatedHours) is { } prioritiesRefusal)
            return new(Refusal: prioritiesRefusal);

        var managementValue = isManagement ?? definition.IsManagement;

        if (slotCount < definition.Assignments.Count
            || definition.Assignments.Any(assignment => assignment.SlotIndex >= slotCount))
            return new(Refusal: $"Cannot reduce slot count to {slotCount} — unassign humans from the slots being removed first");

        if (!string.Equals(definition.Name, name, StringComparison.Ordinal))
        {
            if (RoleNameRefusal(name) is { } nameRefusal)
                return new(Refusal: nameRefusal);
            var lowerName = name.ToLowerInvariant();
            var nameExists = await repo.RoleDefinitionNameExistsAsync(definition.TeamId, lowerName, excludingId: roleDefinitionId, cancellationToken);
            if (nameExists)
                return new(Refusal: $"A role definition with name '{name}' already exists for this team");
        }

        if (managementValue && !definition.IsManagement)
        {
            var existingManagement = await repo.OtherRoleHasIsManagementAsync(
                definition.TeamId, roleDefinitionId, cancellationToken);
            if (existingManagement)
                return new(Refusal: "Another role in this team is already marked as the management role");
        }

        definition.Name = name;
        definition.Description = description;
        definition.SlotCount = slotCount;
        definition.EstimatedHours = estimatedHours;
        definition.Priorities = priorities;
        definition.SortOrder = sortOrder;

        var managementChanged = definition.IsManagement != managementValue;

        definition.IsPublic = isPublic;
        definition.IsManagement = managementValue;
        definition.Period = period;
        definition.UpdatedAt = clock.GetCurrentInstant();

        await repo.PersistRoleDefinitionUpdateAsync(
            definition, managementChanged, cancellationToken);

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamRoleDefinitionUpdated, nameof(TeamRoleDefinition), definition.Id,
            $"Role definition '{name}' updated for team {team.Name}",
            actorUserId,
            targetId: definition.TeamId, targetType: nameof(Team));

        if (managementChanged)
            await SyncRoleAssigneesAsync(definition, cancellationToken);

        return new(Definition: definition);
    }

    public async Task<TeamRoleDefinitionMutationResult> DeleteRoleDefinitionAsync(
        Guid roleDefinitionId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var definition = await repo.FindRoleDefinitionForMutationAsync(roleDefinitionId, cancellationToken);
        if (definition is null)
            return new(Refusal: $"Role definition {roleDefinitionId} not found");
        var team = await GetRoleDefinitionTeamAsync(definition, cancellationToken);

        if (definition.IsManagement && definition.Assignments.Count > 0)
            return new(Refusal: "Cannot delete the management role while members are assigned to it. Unassign all members first.");

        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            return new(Refusal: "User does not have permission to manage role definitions for this team");

        await repo.RemoveRoleDefinitionAsync(definition, cancellationToken);

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamRoleDefinitionDeleted, nameof(TeamRoleDefinition), definition.Id,
            $"Role definition '{definition.Name}' deleted from team {team.Name}",
            actorUserId,
            targetId: definition.TeamId, targetType: nameof(Team));

        return new();
    }

    public async Task<TeamRoleDefinitionMutationResult> ToggleRoleIsManagementAsync(
        Guid roleDefinitionId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var definition = await repo.FindRoleDefinitionForMutationAsync(roleDefinitionId, cancellationToken);
        if (definition is null)
            return new(Refusal: $"Role definition {roleDefinitionId} not found");
        var team = await GetRoleDefinitionTeamAsync(definition, cancellationToken);

        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            return new(Refusal: "User does not have permission to manage role definitions for this team");

        var isManagement = !definition.IsManagement;
        if (isManagement)
        {
            if (definition.Assignments.Count > 0)
                return new(Refusal: "Cannot set IsManagement while members are assigned to the role");

            var existingManagement = await repo.OtherRoleHasIsManagementAsync(
                definition.TeamId, roleDefinitionId, cancellationToken);
            if (existingManagement)
                return new(Refusal: "Another role in this team is already marked as the management role");
        }

        definition.IsManagement = isManagement;
        definition.UpdatedAt = clock.GetCurrentInstant();

        await repo.PersistRoleDefinitionUpdateAsync(
            definition, managementChanged: true, cancellationToken);

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamRoleDefinitionUpdated, nameof(TeamRoleDefinition), definition.Id,
            $"IsManagement set to {isManagement} on role '{definition.Name}' in {team.Name}",
            actorUserId,
            targetId: definition.TeamId, targetType: nameof(Team));

        await SyncRoleAssigneesAsync(definition, cancellationToken);

        return new(Definition: definition);
    }

    private async Task SyncRoleAssigneesAsync(TeamRoleDefinition definition, CancellationToken cancellationToken)
    {
        foreach (var userId in definition.Assignments.Select(a => a.TeamMember.UserId).Distinct())
            await SystemTeamSync.SyncMembershipForUserAsync(userId, SystemTeamType.Coordinators, cancellationToken);
    }

    private static string? RoleNameRefusal(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Role name cannot be empty";

        if (name.EnumerateRunes().Count() > 100)
            return "Role name cannot exceed 100 characters";
        return null;
    }

    private static string? RoleCapacityRefusal(int slotCount, List<SlotPriority> priorities, int? estimatedHours)
    {
        if (slotCount < 1)
            return "Slot count must be at least 1";

        if (priorities.Count != slotCount)
            return $"Priorities count ({priorities.Count}) must match slot count ({slotCount})";

        if (priorities.Any(priority => !Enum.IsDefined(priority)))
            return "Each slot priority must be a defined priority value";
        if (string.Join(",", priorities).Length > 500)
            return "There are too many slots with these priorities. Reduce the number of slots.";
        if (estimatedHours is < 0)
            return "Estimated hours cannot be negative";
        return null;
    }

    private async Task<(TeamRoleDefinition? Definition, Team? Team, string? Refusal)> ResolveRoleAssignmentManagementAsync(
        Guid roleDefinitionId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var definition = await repo.FindRoleDefinitionForMutationAsync(roleDefinitionId, cancellationToken);
        if (definition is null)
            return (null, null, $"Role definition {roleDefinitionId} not found");
        var team = await GetRoleDefinitionTeamAsync(definition, cancellationToken);

        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            return (null, null, "User does not have permission to manage role assignments for this team");

        return (definition, team, null);
    }

    public async Task<TeamRoleAssignmentMutationResult> AssignToRoleAsync(
        Guid roleDefinitionId, Guid targetUserId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var (definition, team, refusal) = await ResolveRoleAssignmentManagementAsync(
            roleDefinitionId, actorUserId, cancellationToken);
        if (definition is null || team is null)
            return new(Refusal: refusal);

        var (resolvedUserId, membershipRefusal) = await ResolveMembershipTargetAsync(targetUserId, cancellationToken);
        if (resolvedUserId is null)
            return new(Refusal: membershipRefusal);
        targetUserId = resolvedUserId.Value;
        var existingMember = await repo.FindActiveMemberForMutationAsync(definition.TeamId, targetUserId, cancellationToken);
        var now = clock.GetCurrentInstant();

        TeamMember? autoAddMember = null;
        GoogleSyncOutboxEvent? outboxEvent = null;
        if (existingMember is null)
        {
            if (team.IsSystemTeam)
                return new(Refusal: "Cannot add members to system teams via role assignment; only existing members can be assigned to roles on system teams.");

            autoAddMember = new TeamMember
            {
                Id = Guid.NewGuid(),
                TeamId = definition.TeamId,
                UserId = targetUserId,
                Role = TeamMemberRole.Member,
                JoinedAt = now
            };
            outboxEvent = await BuildOutboxEventAsync(
                autoAddMember.Id, definition.TeamId, targetUserId,
                GoogleSyncOutboxEventTypes.AddUserToTeamResources, cancellationToken);
        }

        var result = await AssignToRoleWithOutboxAsync(
            roleDefinitionId,
            targetUserId,
            actorUserId,
            autoAddMember,
            outboxEvent,
            promoteToCoordinator: definition.IsManagement,
            now,
            cancellationToken);

        if (!result.Succeeded)
            return result;

        if (autoAddMember is not null)
        {
            await auditLogService.LogAsync(
                TeamsAuditActions.TeamMemberAdded, nameof(Team), definition.TeamId,
                $"Auto-added to {team.Name} via role assignment",
                actorUserId,
                targetId: targetUserId, targetType: AuditSubjectTypes.User);
        }

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamRoleAssigned, nameof(TeamRoleDefinition), roleDefinitionId,
            $"Assigned to role '{definition.Name}' in {team.Name}",
            actorUserId,
            targetId: targetUserId, targetType: AuditSubjectTypes.User);

        // Management roles promote: reconcile Coordinators in the mutation.
        await SystemTeamSync.SyncMembershipForUserAsync(targetUserId, SystemTeamType.Coordinators, cancellationToken);

        return result;
    }

    public async Task<TeamRoleAssignmentMutationResult> UnassignFromRoleAsync(
        Guid roleDefinitionId, Guid teamMemberId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var (definition, team, refusal) = await ResolveRoleAssignmentManagementAsync(
            roleDefinitionId, actorUserId, cancellationToken);
        if (definition is null || team is null)
            return new(Refusal: refusal);

        var result = await repo.UnassignFromRoleAsync(
            roleDefinitionId, teamMemberId, cancellationToken);
        if (!result.Succeeded)
            return result;
        var targetUserId = result.Assignment!.TeamMember.UserId;

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamRoleUnassigned, nameof(TeamRoleDefinition), roleDefinitionId,
            $"Unassigned from role '{definition.Name}' in {team.Name}",
            actorUserId,
            targetId: targetUserId, targetType: AuditSubjectTypes.User);

        // Losing a management role can demote: reconcile Coordinators in the mutation.
        await SystemTeamSync.SyncMembershipForUserAsync(targetUserId, SystemTeamType.Coordinators, cancellationToken);
        return result;
    }

    private IRoleWrite RoleWrite => serviceProvider.GetRequiredService<IRoleWrite>();

    // Lazy: the permission service reads this section's cached holdings, so an eager one would close a DI cycle.
    private IPermissionService Permissions => serviceProvider.GetRequiredService<IPermissionService>();

    // Lazy: Users depends on Teams during service resolution.

    /// <summary>Exactly one of the id and the refusal is set.</summary>
    internal async Task<(Guid? UserId, string? Refusal)> ResolveMembershipTargetAsync(Guid userId, CancellationToken ct)
    {
        var user = await UserService.GetUserInfoAsync(userId, ct);
        if (user is null)
            return (null, $"Cannot add account {userId} to a team: it does not exist.");
        if (!user.IsTombstone)
            return (user.Id, null);
        var merged = user.State == UserState.Merged || user.MergedToUserId is not null
            || user.IdentityEmailColumn?.EndsWith("@merged.local", StringComparison.OrdinalIgnoreCase) == true;
        return (null, merged
            ? "Cannot add an account that was merged into another to a team."
            : "Cannot add a deleted account to a team.");
    }

    public async Task<IReadOnlyList<AdminTeamSummary>> GetAdminTeamListAsync(CancellationToken cancellationToken = default)
    {
        var teams = await repo.GetAllForAdminAsync(cancellationToken);
        var resourceSummaries = await TeamResourceService
            .GetTeamResourceSummariesAsync(teams.Select(t => t.Id).ToList(), cancellationToken);

        return teams.Select(team => CreateAdminTeamSummary(team, resourceSummaries)).ToList();
    }

    private static AdminTeamSummary CreateAdminTeamSummary(
        Team team,
        IReadOnlyDictionary<Guid, TeamResourceSummary> resourceSummaries)
    {
        var systemTeamType = team.SystemTeamType != SystemTeamType.None
            ? team.SystemTeamType.ToString()
            : null;
        var resourceSummary = resourceSummaries.TryGetValue(team.Id, out var summary)
            ? summary
            : TeamResourceSummary.Empty;

        return new AdminTeamSummary(
            team.Id,
            team.Name,
            team.Slug,
            team.IsActive,
            team.RequiresApproval,
            team.IsSystemTeam,
            systemTeamType,
            team.Members.Count,
            team.JoinRequests.Count,
            resourceSummary.HasMailGroup,
            team.GoogleGroupEmail,
            resourceSummary.DriveResourceCount,
            team.RoleDefinitions.Sum(role => role.SlotCount),
            team.CreatedAt,
            team.ParentTeamId,
            team.IsHidden);
    }

    public async Task<IReadOnlyDictionary<Guid, TeamInfo>> ResolveTeamMembersAsync(
        IReadOnlyDictionary<Guid, TeamReadSnapshot> teams, CancellationToken ct = default)
    {
        var userIds = teams.Values.SelectMany(t => t.Members.Select(m => m.UserId)).Distinct().ToList();
        var users = userIds.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await UserService.GetUserInfosAsync(userIds, ct);
        return teams.ToDictionary(pair => pair.Key, pair => pair.Value.Team with
        {
            Members = pair.Value.Members.Select(member =>
            {
                users.TryGetValue(member.UserId, out var user);
                return new TeamMemberInfo(
                    member.TeamMemberId, member.UserId, user?.BurnerName ?? string.Empty,
                    user?.Email, member.Role, member.JoinedAt);
            }).ToList(),
        });
    }

    public async Task<IReadOnlyDictionary<Guid, TeamReadSnapshot>> GetTeamSnapshotsAsync(CancellationToken ct = default)
    {
        var teams = await repo.GetAllWithMembersAsync(ct);
        var managementHolders = await repo.GetManagerUserIdsByTeamAsync(ct);
        var roleDefinitionsByTeam = await repo.GetAllRoleDefinitionsByTeamAsync(ct);
        var childIdsByParent = teams
            .Where(t => t.ParentTeamId.HasValue)
            .GroupBy(t => t.ParentTeamId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(t => t.Id).ToList());
        var pendingCounts = await repo.GetPendingCountsByTeamIdsAsync(
            teams.Select(t => t.Id).ToList(), ct);

        return teams.ToDictionary(
            t => t.Id,
            t => BuildTeamSnapshot(t, managementHolders, roleDefinitionsByTeam, childIdsByParent, pendingCounts));
    }

    private static TeamReadSnapshot BuildTeamSnapshot(
        Team team,
        IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> managementHolders,
        IReadOnlyDictionary<Guid, IReadOnlyList<TeamRoleDefinition>> roleDefinitionsByTeam,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> childIdsByParent,
        IReadOnlyDictionary<Guid, int> pendingCounts) => new(new TeamInfo(
        Id: team.Id,
        Name: team.Name,
        Description: team.Description,
        Slug: team.Slug,
        IsActive: team.IsActive,
        IsSystemTeam: team.IsSystemTeam,
        SystemTeamType: team.SystemTeamType,
        RequiresApproval: team.RequiresApproval,
        IsPublicPage: team.IsPublicPage,
        IsHidden: team.IsHidden,
        IsPromotedToDirectory: team.IsPromotedToDirectory,
        CreatedAt: team.CreatedAt,
        Members: [],
        ParentTeamId: team.ParentTeamId,
        GoogleGroupPrefix: team.GoogleGroupPrefix,
        HasBudget: team.HasBudget,
        IsSensitive: team.IsSensitive,
        CustomSlug: team.CustomSlug,
        ManagementRoleHolderUserIds: managementHolders.TryGetValue(team.Id, out var holders) ? holders : null,
        RoleDefinitions: roleDefinitionsByTeam.TryGetValue(team.Id, out var defs)
            ? defs.Select(d => ToRoleDefinitionSnapshot(d, team)).ToList()
            : null,
        ChildTeamIds: childIdsByParent.TryGetValue(team.Id, out var childIds) ? childIds : null,
        ShowCoordinatorsOnPublicPage: team.ShowCoordinatorsOnPublicPage,
        PageContent: team.PageContent,
        CallsToAction: team.CallsToAction,
        PageContentUpdatedAt: team.PageContentUpdatedAt,
        PageContentUpdatedByUserId: team.PageContentUpdatedByUserId,
        PendingRequestCount: pendingCounts.TryGetValue(team.Id, out var pending) ? pending : 0,
        EarlyEntryEnabled: team.EarlyEntryEnabled),
        team.Members.Where(m => m.LeftAt is null)
            .Select(m => new TeamMemberReference(m.Id, m.UserId, m.Role, m.JoinedAt)).ToList());

    internal static TeamRoleDefinitionSnapshot ToRoleDefinitionSnapshot(
        TeamRoleDefinition definition,
        Team? fallbackTeam = null) =>
        new(
            definition.Id,
            definition.TeamId,
            definition.Team?.Name ?? fallbackTeam?.Name ?? string.Empty,
            definition.Team?.Slug ?? fallbackTeam?.Slug ?? string.Empty,
            definition.Name,
            definition.Description,
            definition.SlotCount,
            definition.EstimatedHours,
            definition.Priorities,
            definition.SortOrder,
            definition.IsManagement,
            definition.Period,
            definition.IsPublic,
            definition.Assignments
                .Select(assignment => new TeamRoleAssignmentSnapshot(
                    assignment.TeamMemberId,
                    assignment.SlotIndex,
                    assignment.TeamMember?.UserId))
                .ToList());
    internal async Task<bool> CanUserApproveRequestsForTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await Permissions.HasAsync(userId, TeamsPermissions.Manage, TeamOwner.Of(teamId), cancellationToken);
    }

    public async Task<bool> IsUserCoordinatorOfTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var teamsById = await LoadTeamsByIdAsync(cancellationToken);
        return TeamCoordinatorAccess.IsCoordinatorOfActiveTeam(teamsById, teamId, userId);
    }

    public async Task<IReadOnlyDictionary<Guid, TeamInfo>> LoadTeamsByIdAsync(CancellationToken ct = default) =>
        await ResolveTeamMembersAsync(await GetTeamSnapshotsAsync(ct), ct);

    internal async Task<Team> GetRoleDefinitionTeamAsync(TeamRoleDefinition definition, CancellationToken ct)
    {
        var teamsById = await repo.GetByIdsWithParentsAsync([definition.TeamId], ct);
        return teamsById.TryGetValue(definition.TeamId, out var team)
            ? team
            : throw new InvalidOperationException($"Team {definition.TeamId} not found");
    }

    public async Task<IReadOnlyList<TeamRoleDefinitionSnapshot>> GetRoleDefinitionsAsync(
        Guid teamId, CancellationToken cancellationToken = default)
    {
        var definitions = await repo.GetRoleDefinitionsAsync(teamId, cancellationToken);
        return definitions.Select(definition => ToRoleDefinitionSnapshot(definition)).ToList();
    }

    public async Task<TeamInfo?> GetTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        var teamsById = await LoadTeamsByIdAsync(cancellationToken);
        return teamsById.GetValueOrDefault(teamId);
    }

    public async Task<IReadOnlyList<TeamJoinRequestSnapshot>> GetPendingRequestsForTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        var requests = await repo.GetPendingForTeamAsync(teamId, cancellationToken);
        return requests.Select(ToJoinRequestSnapshot).ToList();
    }

    public async Task<IReadOnlyList<TeamJoinRequestSnapshot>> GetPendingRequestsForUserAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var requests = await repo.GetAllJoinRequestsForUserAsync(userId, cancellationToken);
        return requests.Where(request => request.Status == TeamJoinRequestStatus.Pending)
            .Select(ToJoinRequestSnapshot).ToList();
    }

    public async Task<TeamJoinRequestSnapshot?> GetUserPendingRequestAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var request = await repo.FindUserPendingRequestAsync(teamId, userId, cancellationToken);
        return request is null ? null : ToJoinRequestSnapshot(request);
    }

    private static TeamJoinRequestSnapshot ToJoinRequestSnapshot(TeamJoinRequest request) => new(
        request.Id,
        request.TeamId,
        request.Team?.Name,
        request.UserId,
        request.Message,
        request.RequestedAt,
        request.Team?.Slug);

    public async Task<IReadOnlyList<UserTeamMembershipInfo>> GetUserTeamMembershipsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var memberships = await repo.GetActiveByUserIdAsync(userId, cancellationToken);
        return memberships
            .Select(m => new UserTeamMembershipInfo(
                m.TeamId, m.Team.Name, m.Role)
            {
                IsActive = m.Team.IsActive,
                IsHidden = m.Team.IsHidden,
                SystemTeamType = m.Team.SystemTeamType
            })
            .ToList();
    }
}
