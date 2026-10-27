using System.Collections.Concurrent;
using Humans.Base.Caching;
using Humans.Base.Interfaces;
using Humans.Base.Extensions;
using Humans.Teams.Contracts;
using Humans.Teams.Domain;
using Humans.Base.Enums;
using Humans.Gdpr.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Teams.Services;

/// <summary>
/// Caches only Teams-owned snapshots; member display fields are resolved through Users on each read. Serves bulk
/// reads, so /Debug/CacheStats records no per-key hits or misses: entry and invalidation counts are the meaningful
/// diagnostics.
/// </summary>
internal sealed class CachingTeamService(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingTeamService> logger) : TrackedCache<Guid, TeamReadSnapshot>("Team.TeamInfo", warmOnStartup: true, logger),
    ITeamManagementService, ITeamSeeding, IUserMerge, IUserDataContributor, IEntityNameContributor
{
    public const string InnerServiceKey = "team-inner";

    /// <summary>
    /// Inverse of the primary cache, rebuilt from scratch by every warm. Invalidation drops both, so they stay
    /// consistent without per-mutation diffing. Written only during warmup; readers iterate without locking.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, HashSet<Guid>> _teamIdsByUserId = new();

    public Task<TeamUpdateResult> CreateTeamWithGoogleGroupAsync(
        string name,
        string? description,
        bool requiresApproval,
        Guid? parentTeamId = null,
        string? googleGroupPrefix = null,
        bool isHidden = false,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.CreateTeamWithGoogleGroupAsync(
            name, description, requiresApproval, parentTeamId, googleGroupPrefix, isHidden, cancellationToken));

    public Task<TeamUpdateResult> UpdateTeamWithGoogleGroupAsync(
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
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.UpdateTeamWithGoogleGroupAsync(
            teamId, name, description, requiresApproval, isActive, parentTeamId,
            googleGroupPrefix, customSlug, hasBudget, isHidden, isSensitive,
            isPromotedToDirectory, earlyEntryEnabled, cancellationToken));

    public Task<Team> CreateTeamAsync(
        string name,
        string? description,
        bool requiresApproval,
        Guid? parentTeamId = null,
        string? googleGroupPrefix = null,
        bool isHidden = false,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.CreateTeamAsync(
            name, description, requiresApproval, parentTeamId, googleGroupPrefix, isHidden, cancellationToken));

    public async Task<TeamInfo?> GetTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        var teamsById = await GetTeamsByIdAsync(cancellationToken);
        return teamsById.GetValueOrDefault(teamId);
    }

    public async Task<TeamInfo?> GetTeamBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        // Same canonical and custom slug semantics as the team page.
        var teamsById = await GetTeamsByIdAsync(cancellationToken);
        return TeamSlugResolver.Find(teamsById, slug);
    }

    public async Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamsAsync(
        CancellationToken cancellationToken = default) =>
        await GetTeamsByIdAsync(cancellationToken);

    /// <summary>Ids Teams doesn't own are absent.</summary>
    public async Task<IReadOnlyDictionary<Guid, EntityName>> ResolveNamesAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return new Dictionary<Guid, EntityName>();

        var teams = await GetTeamsByIdAsync(ct);
        var result = new Dictionary<Guid, EntityName>();
        foreach (var id in ids)
        {
            if (teams.TryGetValue(id, out var team))
                result[id] = new EntityName("Team", team.Name, team.Slug);
        }
        return result;
    }

    public async Task<IReadOnlyList<TeamSearchHit>> SearchAsync(
        string query, int max,
        CancellationToken cancellationToken = default)
    {
        // Served entirely from the cache: search must never hit the database.
        if (string.IsNullOrWhiteSpace(query) || max <= 0)
            return [];

        var teamsById = await GetTeamsByIdAsync(cancellationToken);

        // A pasted GUID jumps straight to that team, whatever its visibility.
        if (Guid.TryParse(query, out var id))
        {
            return teamsById.TryGetValue(id, out var byId)
                ? [new TeamSearchHit(byId.Id, byId.Name, StringSearchExtensions.ExactNameScore)]
                : [];
        }

        // Name match: active and non-hidden, case-insensitive contains.
        var trimmed = query.Trim();
        return teamsById.Values
            .Where(t => t.IsActive && !t.IsHidden
                && t.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(t => new TeamSearchHit(t.Id, t.Name, t.Name.NameMatchScore(trimmed)))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserTeamMembershipInfo>> GetUserTeamMembershipsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await EnsureWarmedAsync(cancellationToken);

        if (!_teamIdsByUserId.TryGetValue(userId, out var teamIds) || teamIds.Count == 0)
            return [];

        var snapshot = AsReadOnlyDictionary;
        var result = new List<UserTeamMembershipInfo>(teamIds.Count);
        foreach (var teamId in teamIds)
        {
            if (!snapshot.TryGetValue(teamId, out var team))
                continue;

            var membership = team.Members.FirstOrDefault(m => m.UserId == userId);
            if (membership is null)
                continue;

            result.Add(new UserTeamMembershipInfo(
                membership.TeamMemberId, team.Team.Id, team.Team.Name, team.Team.Slug, team.Team.IsSystemTeam,
                membership.Role, membership.JoinedAt));
        }

        return result;
    }

    public Task<TeamUpdateResult> UpdateTeamAsync(
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
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.UpdateTeamAsync(
            teamId, name, description, requiresApproval, isActive, parentTeamId,
            googleGroupPrefix, customSlug, hasBudget, isHidden, isSensitive,
            isPromotedToDirectory, earlyEntryEnabled, cancellationToken));

    // Early-entry grants don't touch the team cache.

    public Task<IReadOnlyList<TeamEarlyEntryGrantInfo>> GetEarlyEntryGrantsForTeamAsync(Guid teamId, CancellationToken ct = default)
        => WithInnerAsync(inner => inner.GetEarlyEntryGrantsForTeamAsync(teamId, ct));

    public Task AddEarlyEntryGrantAsync(Guid teamId, Guid userId, LocalDate entryDate, string projectName, Guid actorUserId, CancellationToken ct = default)
        => WithInnerAsync(inner => inner.AddEarlyEntryGrantAsync(teamId, userId, entryDate, projectName, actorUserId, ct));

    public Task EditEarlyEntryGrantAsync(Guid teamId, Guid grantId, LocalDate entryDate, string projectName, Guid actorUserId, CancellationToken ct = default)
        => WithInnerAsync(inner => inner.EditEarlyEntryGrantAsync(teamId, grantId, entryDate, projectName, actorUserId, ct));

    public Task RemoveEarlyEntryGrantAsync(Guid teamId, Guid grantId, Guid actorUserId, CancellationToken ct = default)
        => WithInnerAsync(inner => inner.RemoveEarlyEntryGrantAsync(teamId, grantId, actorUserId, ct));

    public Task<TeamDeactivationResult> DeleteTeamAsync(Guid teamId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.DeleteTeamAsync(teamId, actorUserId, cancellationToken), static result => result.Succeeded);

    public Task<TeamJoinResult> JoinTeamAsync(
        Guid teamId,
        Guid userId,
        string? message,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.JoinTeamAsync(teamId, userId, message, cancellationToken));

    public Task<TeamMembershipMutationResult> LeaveTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.LeaveTeamAsync(teamId, userId, cancellationToken));

    public Task<TeamMembershipMutationResult> WithdrawJoinRequestAsync(
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.WithdrawJoinRequestAsync(requestId, userId, cancellationToken));

    public Task<TeamJoinReviewResult> ApproveJoinRequestAsync(
        Guid requestId,
        Guid approverUserId,
        string? notes,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.ApproveJoinRequestAsync(
            requestId, approverUserId, notes, cancellationToken));

    public Task<TeamJoinReviewResult> RejectJoinRequestAsync(
        Guid requestId,
        Guid approverUserId,
        string reason,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.RejectJoinRequestAsync(requestId, approverUserId, reason, cancellationToken));

    public Task<IReadOnlyList<TeamJoinRequestSnapshot>> GetPendingRequestsForTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default) =>
        WithInnerAsync(inner => inner.GetPendingRequestsForTeamAsync(teamId, cancellationToken));

    public Task<IReadOnlyList<TeamJoinRequestSnapshot>> GetPendingRequestsForUserAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        WithInnerAsync(inner => inner.GetPendingRequestsForUserAsync(userId, cancellationToken));

    public Task<TeamJoinRequestSnapshot?> GetUserPendingRequestAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default) =>
        WithInnerAsync(inner => inner.GetUserPendingRequestAsync(teamId, userId, cancellationToken));

    public async Task<bool> IsUserCoordinatorOfTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var teamsById = await GetTeamsByIdAsync(cancellationToken);
        return TeamCoordinatorAccess.IsCoordinatorOfActiveTeam(teamsById, teamId, userId);
    }

    public Task<TeamMemberRemovalResult> RemoveMemberAsync(
        Guid teamId,
        Guid userId,
        Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.RemoveMemberAsync(
            teamId, userId, actorUserId, cancellationToken));

    public async Task<IReadOnlyDictionary<Guid, string>> GetManagementRoleNamesByTeamIdsAsync(
        IEnumerable<Guid> teamIds,
        CancellationToken cancellationToken = default)
    {
        // The definition flagged both IsManagement and IsPublic; teams without one are omitted.
        var teamsById = await GetTeamsByIdAsync(cancellationToken);
        var result = new Dictionary<Guid, string>();
        foreach (var teamId in teamIds)
        {
            if (!teamsById.TryGetValue(teamId, out var team) || team.RoleDefinitions is null)
                continue;
            var mgmt = team.RoleDefinitions.FirstOrDefault(d => d.IsManagement && d.IsPublic);
            if (mgmt is not null)
                result[teamId] = mgmt.Name;
        }
        return result;
    }

    public Task<(bool Updated, string? PreviousPrefix)> SetGoogleGroupPrefixAsync(
        Guid teamId,
        string? prefix,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.SetGoogleGroupPrefixAsync(teamId, prefix, cancellationToken), static result => result.Updated);

    public Task<AdminTeamListResult> GetAdminTeamListAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        WithInnerAsync(inner => inner.GetAdminTeamListAsync(page, pageSize, cancellationToken));

    public Task<TeamJoinReviewResult> AddMemberToTeamAsync(
        Guid teamId,
        Guid targetUserId,
        Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.AddMemberToTeamAsync(
            teamId, targetUserId, actorUserId, cancellationToken));

    public Task SetMemberRoleAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.SetMemberRoleAsync(teamId, userId, role, actorUserId, cancellationToken));

    public Task<TeamPageUpdateResult> UpdateTeamPageContentAsync(
        Guid teamId,
        string? pageContent,
        IReadOnlyList<TeamPageCallToActionInput> callsToAction,
        bool isPublicPage,
        bool showCoordinatorsOnPublicPage,
        Guid updatedByUserId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.UpdateTeamPageContentAsync(
            teamId, pageContent, callsToAction, isPublicPage,
            showCoordinatorsOnPublicPage, updatedByUserId, cancellationToken));

    public Task<TeamRoleDefinition> CreateRoleDefinitionAsync(
        Guid teamId,
        string name,
        string? description,
        int slotCount,
        List<SlotPriority> priorities,
        int sortOrder,
        RolePeriod period,
        Guid actorUserId,
        bool isPublic = true,
        int? estimatedHours = null,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.CreateRoleDefinitionAsync(
            teamId, name, description, slotCount, priorities, sortOrder,
            period, actorUserId, isPublic, estimatedHours, cancellationToken));

    public Task<TeamRoleDefinition> UpdateRoleDefinitionAsync(
        Guid roleDefinitionId,
        string name,
        string? description,
        int slotCount,
        List<SlotPriority> priorities,
        int sortOrder,
        bool isManagement,
        RolePeriod period,
        Guid actorUserId,
        bool isPublic = true,
        bool canToggleManagement = true,
        int? estimatedHours = null,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.UpdateRoleDefinitionAsync(
            roleDefinitionId, name, description, slotCount, priorities, sortOrder,
            isManagement, period, actorUserId, isPublic, canToggleManagement, estimatedHours, cancellationToken));

    public Task DeleteRoleDefinitionAsync(
        Guid roleDefinitionId,
        Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.DeleteRoleDefinitionAsync(roleDefinitionId, actorUserId, cancellationToken));

    public Task<TeamRoleManagementToggleResult> ToggleRoleIsManagementAsync(
        Guid roleDefinitionId,
        Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.ToggleRoleIsManagementAsync(
            roleDefinitionId, actorUserId, cancellationToken));

    public async Task<IReadOnlyList<TeamRoleDefinitionSnapshot>> GetRoleDefinitionsAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        // No activity or system filter; the projection is pre-sorted at warm time.
        var teamsById = await GetTeamsByIdAsync(cancellationToken);
        if (!teamsById.TryGetValue(teamId, out var team) || team.RoleDefinitions is null)
            return [];
        return team.RoleDefinitions;
    }

    public Task<TeamRoleAssignment> AssignToRoleAsync(
        Guid roleDefinitionId,
        Guid targetUserId,
        Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.AssignToRoleAsync(
            roleDefinitionId, targetUserId, actorUserId, cancellationToken));

    public Task UnassignFromRoleAsync(
        Guid roleDefinitionId,
        Guid teamMemberId,
        Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.UnassignFromRoleAsync(
            roleDefinitionId, teamMemberId, actorUserId, cancellationToken));

    public async Task<IReadOnlyList<Guid>> GetUserCoordinatedTeamIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Non-system teams where the user is an active Coordinator or holds an active management role.
        var teamsById = await GetTeamsByIdAsync(cancellationToken);
        var result = new HashSet<Guid>();
        foreach (var team in teamsById.Values)
        {
            if (team.SystemTeamType != SystemTeamType.None)
                continue;

            if (team.Members.Any(m => m.UserId == userId && m.Role == TeamMemberRole.Coordinator))
            {
                result.Add(team.Id);
                continue;
            }

            if (team.ManagementRoleHolderUserIds?.Contains(userId) == true)
                result.Add(team.Id);
        }
        return result.ToList();
    }

    public Task EnqueueGoogleResyncForUserTeamsAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        WithInnerAsync(inner => inner.EnqueueGoogleResyncForUserTeamsAsync(userId, cancellationToken));

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamsWithParentsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetTeamsByIdAsync(cancellationToken);
        var result = new Dictionary<Guid, TeamInfo>();
        foreach (var teamId in teamIds)
        {
            if (!snapshot.TryGetValue(teamId, out var team))
                continue;
            result[teamId] = team;
            if (team.ParentTeamId is { } parentTeamId
                && snapshot.TryGetValue(parentTeamId, out var parent))
            {
                result[parentTeamId] = parent;
            }
        }
        return result;
    }

    // ITeamSeeding is implemented explicitly: the internal members of the same names return entities, which can't
    // leave the assembly.

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
            ?? throw new InvalidOperationException($"Team {team.Id} missing from the cache after creation");
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

    public Task<TeamMember> AddSeededMemberAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Instant joinedAt,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.AddSeededMemberAsync(
            teamId, userId, role, joinedAt, cancellationToken));

    public Task<bool> PermanentlyDeleteTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.PermanentlyDeleteTeamAsync(teamId, cancellationToken), static result => result);

    public async Task<IReadOnlyCollection<Guid>> GetEffectiveBudgetCoordinatorTeamIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Departments (no parent, no system-team filter) where the user is Coordinator or holds a management role,
        // plus their active children.
        var teamsById = await GetTeamsByIdAsync(cancellationToken);
        var departments = new HashSet<Guid>();
        foreach (var team in teamsById.Values)
        {
            if (team.ParentTeamId.HasValue)
                continue;

            if (team.Members.Any(m => m.UserId == userId && m.Role == TeamMemberRole.Coordinator)
                || team.ManagementRoleHolderUserIds?.Contains(userId) == true)
            {
                departments.Add(team.Id);
            }
        }

        if (departments.Count == 0)
            return departments;

        var result = new HashSet<Guid>(departments);
        foreach (var team in teamsById.Values)
        {
            if (team.IsActive
                && team.ParentTeamId.HasValue
                && departments.Contains(team.ParentTeamId.Value))
            {
                result.Add(team.Id);
            }
        }
        return result;
    }

    public void InvalidateActiveTeamsCache() => InvalidateTeamsCache();

    private void InvalidateTeamsCache() => Clear();

    public Task<int> RevokeAllMembershipsAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.RevokeAllMembershipsAsync(userId, cancellationToken), static result => result > 0);

    public Task<IReadOnlyList<TeamRoleReconciliationMembership>> GetActiveMembershipsForRoleReconciliationAsync(
        CancellationToken cancellationToken = default) =>
        WithInnerAsync(inner => inner.GetActiveMembershipsForRoleReconciliationAsync(cancellationToken));

    public Task<int> ApplyMemberRoleChangesAsync(
        IReadOnlyCollection<(Guid TeamMemberId, TeamMemberRole Role)> changes,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.ApplyMemberRoleChangesAsync(changes, cancellationToken), static result => result > 0);

    public Task<bool> ApplySystemTeamMembershipDeltaAsync(
        Guid teamId,
        IReadOnlyCollection<Guid> userIdsToAdd,
        IReadOnlyCollection<Guid> userIdsToRemove,
        Instant now,
        CancellationToken cancellationToken = default) =>
        MutateAsync(inner => inner.ApplySystemTeamMembershipDeltaAsync(
            teamId, userIdsToAdd, userIdsToRemove, now, cancellationToken), static result => result);

    public async Task ReassignAsync(
        Guid mergedFromUserId,
        Guid mergedToUserId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct)
    {
        try
        {
            await WithInnerMergeAsync(inner => inner.ReassignAsync(
                mergedFromUserId, mergedToUserId, actorUserId, now, ct));
        }
        finally
        {
            InvalidateTeamsCache();
        }
    }

    // Static table: the erasure-coverage architecture test reads it from an uninitialized instance.
    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [TeamService.TeamMemberships] =
                "Partially retained: the membership row (team, role, joined/left dates, keyed to " +
                "the user id the Users section tombstones) is the association's record of who " +
                "worked on what — Ley Orgánica 1/2002 Art. 14, GDPR Art. 17(3)(b). Every still-" +
                "active membership is ended immediately.",
            [TeamService.TeamJoinRequests] = null,
            [TeamService.TeamEarlyEntry] = null
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        WithInnerAsync(inner => inner.ContributeForUserAsync(userId, ct));

    /// <summary>Evicts even when a later step of the inner erase throws.</summary>
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        MutateAsync(inner => inner.EraseForUserAsync(userId, ct));

    private async Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamsByIdAsync(CancellationToken ct)
    {
        var teams = await GetTeamSnapshotsAsync(ct);
        return await ResolveTeamMembersAsync(teams, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, TeamReadSnapshot>> GetTeamSnapshotsAsync(CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct);
        return AsReadOnlyDictionary;
    }

    public Task<IReadOnlyDictionary<Guid, TeamInfo>> ResolveTeamMembersAsync(
        IReadOnlyDictionary<Guid, TeamReadSnapshot> teams, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.ResolveTeamMembersAsync(teams, ct));

    protected override async Task WarmAllAsync(CancellationToken ct)
    {
        var teams = await WithInnerAsync(inner => inner.GetTeamSnapshotsAsync(ct));

        foreach (var (teamId, team) in teams)
            Set(teamId, team);

        _teamIdsByUserId.Clear();
        foreach (var team in teams.Values)
        {
            foreach (var member in team.Members)
            {
                var set = _teamIdsByUserId.GetOrAdd(member.UserId, static _ => new HashSet<Guid>());
                set.Add(team.Team.Id);
            }
        }
    }

    // A write can commit before later audit/provisioning work fails. An exception must
    // evict the read model too; a completed no-op keeps its existing invalidation policy.
    private async Task<TResult> MutateAsync<TResult>(
        Func<ITeamManagementService, Task<TResult>> action,
        Func<TResult, bool>? changed = null)
    {
        var invalidate = true;
        try
        {
            var result = await WithInnerAsync(action);
            invalidate = changed?.Invoke(result) ?? true;
            return result;
        }
        finally
        {
            if (invalidate)
                InvalidateTeamsCache();
        }
    }

    private async Task MutateAsync(Func<ITeamManagementService, Task> action)
    {
        try
        {
            await WithInnerAsync(action);
        }
        finally
        {
            InvalidateTeamsCache();
        }
    }

    private async Task<TResult> WithInnerAsync<TResult>(Func<ITeamManagementService, Task<TResult>> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ITeamManagementService>(InnerServiceKey);
        return await action(inner);
    }

    private async Task WithInnerAsync(Func<ITeamManagementService, Task> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ITeamManagementService>(InnerServiceKey);
        await action(inner);
    }

    private async Task WithInnerMergeAsync(Func<IUserMerge, Task> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IUserMerge>(InnerServiceKey);
        await action(inner);
    }
}
