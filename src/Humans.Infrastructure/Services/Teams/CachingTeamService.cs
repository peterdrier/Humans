using System.Collections.Concurrent;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Teams;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Services.Teams;

/// <summary>
/// Transparent singleton decorator for <see cref="ITeamService"/> team reads.
/// The scoped inner service owns Teams behavior and writes; this decorator owns the
/// process-local team read model and invalidates it after writes.
/// </summary>
public sealed class CachingTeamService : ITeamService, IUserMerge
{
    public const string InnerServiceKey = "team-inner";

    private readonly ITeamRepository _teamRepository;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CachingTeamService> _logger;
    private readonly ConcurrentDictionary<Guid, TeamInfo> _byTeamId = new();
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile bool _isLoaded;

    public CachingTeamService(
        ITeamRepository teamRepository,
        IServiceScopeFactory scopeFactory,
        ILogger<CachingTeamService> logger)
    {
        _teamRepository = teamRepository;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<Team> CreateTeamAsync(
        string name,
        string? description,
        bool requiresApproval,
        Guid? parentTeamId = null,
        string? googleGroupPrefix = null,
        bool isHidden = false,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.CreateTeamAsync(
            name, description, requiresApproval, parentTeamId, googleGroupPrefix, isHidden, cancellationToken));
        InvalidateTeamsCache();
        return result;
    }

    public Task<Team?> GetTeamBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetTeamBySlugAsync(slug, cancellationToken));

    public Task<Team?> GetTeamByIdAsync(Guid teamId, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetTeamByIdAsync(teamId, cancellationToken));

    public async Task<TeamInfo?> GetTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        var teamsById = await GetTeamsByIdAsync(cancellationToken);
        return teamsById.GetValueOrDefault(teamId);
    }

    public async Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamsAsync(
        CancellationToken cancellationToken = default) =>
        await GetTeamsByIdAsync(cancellationToken);

    public Task<string?> GetTeamNameByGoogleGroupPrefixAsync(
        string googleGroupPrefix,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetTeamNameByGoogleGroupPrefixAsync(googleGroupPrefix, cancellationToken));

    public Task<IReadOnlyDictionary<Guid, string>> GetTeamNamesByIdsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetTeamNamesByIdsAsync(teamIds, cancellationToken));

    public Task<IReadOnlyList<Team>> GetAllTeamsAsync(CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetAllTeamsAsync(cancellationToken));

    public Task<IReadOnlyList<TeamSearchHit>> SearchAsync(
        string query, int max,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.SearchAsync(query, max, cancellationToken));

    public async Task<TeamDirectoryResult> GetTeamDirectoryAsync(
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        var teamsById = await GetTeamsByIdAsync(cancellationToken);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var roleAssignmentService = scope.ServiceProvider.GetRequiredService<IRoleAssignmentService>();
        return await TeamDirectoryBuilder.BuildAsync(
            teamsById,
            roleAssignmentService,
            userId,
            cancellationToken);
    }

    public Task<TeamDetailResult?> GetTeamDetailAsync(
        string slug,
        Guid? userId,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetTeamDetailAsync(slug, userId, cancellationToken));

    public Task<IReadOnlyList<TeamMember>> GetUserTeamsAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetUserTeamsAsync(userId, cancellationToken));

    public Task<IReadOnlyList<MyTeamMembershipSummary>> GetMyTeamMembershipsAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetMyTeamMembershipsAsync(userId, cancellationToken));

    public async Task<Team> UpdateTeamAsync(
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
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.UpdateTeamAsync(
            teamId, name, description, requiresApproval, isActive, parentTeamId,
            googleGroupPrefix, customSlug, hasBudget, isHidden, isSensitive,
            isPromotedToDirectory, cancellationToken));
        InvalidateTeamsCache();
        return result;
    }

    public async Task DeleteTeamAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.DeleteTeamAsync(teamId, cancellationToken));
        InvalidateTeamsCache();
    }

    public Task<TeamJoinRequest> RequestToJoinTeamAsync(
        Guid teamId,
        Guid userId,
        string? message,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.RequestToJoinTeamAsync(teamId, userId, message, cancellationToken));

    public async Task<TeamMember> JoinTeamDirectlyAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.JoinTeamDirectlyAsync(teamId, userId, cancellationToken));
        InvalidateTeamsCache();
        return result;
    }

    public async Task<bool> LeaveTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.LeaveTeamAsync(teamId, userId, cancellationToken));
        InvalidateTeamsCache();
        return result;
    }

    public Task WithdrawJoinRequestAsync(
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.WithdrawJoinRequestAsync(requestId, userId, cancellationToken));

    public async Task<TeamMember> ApproveJoinRequestAsync(
        Guid requestId,
        Guid approverUserId,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.ApproveJoinRequestAsync(
            requestId, approverUserId, notes, cancellationToken));
        InvalidateTeamsCache();
        return result;
    }

    public Task RejectJoinRequestAsync(
        Guid requestId,
        Guid approverUserId,
        string reason,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.RejectJoinRequestAsync(requestId, approverUserId, reason, cancellationToken));

    public Task<IReadOnlyList<TeamJoinRequest>> GetPendingRequestsForApproverAsync(
        Guid approverUserId,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetPendingRequestsForApproverAsync(approverUserId, cancellationToken));

    public Task<IReadOnlyList<TeamJoinRequest>> GetPendingRequestsForTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetPendingRequestsForTeamAsync(teamId, cancellationToken));

    public Task<TeamJoinRequest?> GetUserPendingRequestAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetUserPendingRequestAsync(teamId, userId, cancellationToken));

    public Task<bool> CanUserApproveRequestsForTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.CanUserApproveRequestsForTeamAsync(teamId, userId, cancellationToken));

    public async Task<bool> IsUserMemberOfTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var teamsById = await GetTeamsByIdAsync(cancellationToken);
        return teamsById.TryGetValue(teamId, out var team)
            && team.IsActive
            && team.Members.Any(m => m.UserId == userId);
    }

    public async Task<bool> IsUserCoordinatorOfTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var teamsById = await GetTeamsByIdAsync(cancellationToken);
        var coordinatorTeamIds = await _teamRepository.GetUserCoordinatorTeamIdsAsync(userId, cancellationToken);
        return IsUserCoordinatorOfActiveTeam(teamsById, coordinatorTeamIds, teamId, userId);
    }

    public async Task<bool> RemoveMemberAsync(
        Guid teamId,
        Guid userId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.RemoveMemberAsync(
            teamId, userId, actorUserId, cancellationToken));
        InvalidateTeamsCache();
        return result;
    }

    public Task<IReadOnlyDictionary<Guid, int>> GetPendingRequestCountsByTeamIdsAsync(
        IEnumerable<Guid> teamIds,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetPendingRequestCountsByTeamIdsAsync(teamIds, cancellationToken));

    public Task<IReadOnlyDictionary<Guid, string>> GetManagementRoleNamesByTeamIdsAsync(
        IEnumerable<Guid> teamIds,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetManagementRoleNamesByTeamIdsAsync(teamIds, cancellationToken));

    public Task<IReadOnlyDictionary<Guid, List<string>>> GetNonSystemTeamNamesByUserIdsAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetNonSystemTeamNamesByUserIdsAsync(userIds, cancellationToken));

    public Task<IReadOnlyList<TeamOptionDto>> GetActiveTeamOptionsAsync(
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetActiveTeamOptionsAsync(cancellationToken));

    public async Task<(bool Updated, string? PreviousPrefix)> SetGoogleGroupPrefixAsync(
        Guid teamId,
        string? prefix,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.SetGoogleGroupPrefixAsync(teamId, prefix, cancellationToken));
        if (result.Updated)
            InvalidateTeamsCache();
        return result;
    }

    public Task<(IReadOnlyList<Team> Items, int TotalCount)> GetAllTeamsForAdminAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetAllTeamsForAdminAsync(page, pageSize, cancellationToken));

    public Task<AdminTeamListResult> GetAdminTeamListAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetAdminTeamListAsync(page, pageSize, cancellationToken));

    public Task<IReadOnlyList<TeamRosterSlotSummary>> GetRosterAsync(
        string? priority,
        string? status,
        string? period,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetRosterAsync(priority, status, period, cancellationToken));

    public async Task<TeamMember> AddMemberToTeamAsync(
        Guid teamId,
        Guid targetUserId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.AddMemberToTeamAsync(
            teamId, targetUserId, actorUserId, cancellationToken));
        InvalidateTeamsCache();
        return result;
    }

    public async Task SetMemberRoleAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.SetMemberRoleAsync(teamId, userId, role, actorUserId, cancellationToken));
        InvalidateTeamsCache();
    }

    public async Task UpdateTeamPageContentAsync(
        Guid teamId,
        string? pageContent,
        List<CallToAction> callsToAction,
        bool isPublicPage,
        bool showCoordinatorsOnPublicPage,
        Guid updatedByUserId,
        CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.UpdateTeamPageContentAsync(
            teamId, pageContent, callsToAction, isPublicPage,
            showCoordinatorsOnPublicPage, updatedByUserId, cancellationToken));
        InvalidateTeamsCache();
    }

    public async Task<TeamRoleDefinition> CreateRoleDefinitionAsync(
        Guid teamId,
        string name,
        string? description,
        int slotCount,
        List<SlotPriority> priorities,
        int sortOrder,
        RolePeriod period,
        Guid actorUserId,
        bool isPublic = true,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.CreateRoleDefinitionAsync(
            teamId, name, description, slotCount, priorities, sortOrder,
            period, actorUserId, isPublic, cancellationToken));
        InvalidateTeamsCache();
        return result;
    }

    public async Task<TeamRoleDefinition> UpdateRoleDefinitionAsync(
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
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.UpdateRoleDefinitionAsync(
            roleDefinitionId, name, description, slotCount, priorities, sortOrder,
            isManagement, period, actorUserId, isPublic, cancellationToken));
        InvalidateTeamsCache();
        return result;
    }

    public async Task DeleteRoleDefinitionAsync(
        Guid roleDefinitionId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.DeleteRoleDefinitionAsync(roleDefinitionId, actorUserId, cancellationToken));
        InvalidateTeamsCache();
    }

    public async Task SetRoleIsManagementAsync(
        Guid roleDefinitionId,
        bool isManagement,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.SetRoleIsManagementAsync(
            roleDefinitionId, isManagement, actorUserId, cancellationToken));
        InvalidateTeamsCache();
    }

    public Task<IReadOnlyList<TeamRoleDefinition>> GetRoleDefinitionsAsync(
        Guid teamId,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetRoleDefinitionsAsync(teamId, cancellationToken));

    public Task<IReadOnlyList<TeamRoleDefinition>> GetAllRoleDefinitionsAsync(
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetAllRoleDefinitionsAsync(cancellationToken));

    public async Task<TeamRoleAssignment> AssignToRoleAsync(
        Guid roleDefinitionId,
        Guid targetUserId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.AssignToRoleAsync(
            roleDefinitionId, targetUserId, actorUserId, cancellationToken));
        InvalidateTeamsCache();
        return result;
    }

    public async Task UnassignFromRoleAsync(
        Guid roleDefinitionId,
        Guid teamMemberId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.UnassignFromRoleAsync(
            roleDefinitionId, teamMemberId, actorUserId, cancellationToken));
        InvalidateTeamsCache();
    }

    public Task<IReadOnlyList<Guid>> GetUserCoordinatedTeamIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetUserCoordinatedTeamIdsAsync(userId, cancellationToken));

    public Task<IReadOnlyList<Guid>> GetCoordinatorUserIdsAsync(
        Guid teamId,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetCoordinatorUserIdsAsync(teamId, cancellationToken));

    public async Task<IReadOnlyList<Humans.Application.Models.TeamMembership>> GetActiveTeamMembershipsForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var teamsById = await GetTeamsByIdAsync(cancellationToken);
        var rows = new List<Humans.Application.Models.TeamMembership>();

        foreach (var team in teamsById.Values.Where(t => t.IsActive))
        {
            if (team.SystemTeamType == SystemTeamType.Volunteers)
                continue;

            var membership = team.Members.FirstOrDefault(m => m.UserId == userId);
            if (membership is not null)
                rows.Add(new Humans.Application.Models.TeamMembership(team.Name, membership.Role));
        }

        return rows;
    }

    public Task EnqueueGoogleResyncForUserTeamsAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.EnqueueGoogleResyncForUserTeamsAsync(userId, cancellationToken));

    public Task<IReadOnlyDictionary<Guid, Team>> GetByIdsWithParentsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetByIdsWithParentsAsync(teamIds, cancellationToken));

    public Task<IReadOnlyList<TeamCoordinatorRef>> GetActiveCoordinatorsForTeamsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetActiveCoordinatorsForTeamsAsync(teamIds, cancellationToken));

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetActiveNonSystemTeamNamesByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        var userIdSet = userIds.ToHashSet();
        if (userIdSet.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<string>>();

        var teamsById = await GetTeamsByIdAsync(cancellationToken);
        var result = new Dictionary<Guid, List<string>>();

        foreach (var team in teamsById.Values.Where(t => t.IsActive && t.SystemTeamType == SystemTeamType.None && !t.IsHidden))
        {
            foreach (var member in team.Members.Where(m => userIdSet.Contains(m.UserId)))
            {
                if (!result.TryGetValue(member.UserId, out var names))
                {
                    names = [];
                    result[member.UserId] = names;
                }
                names.Add(team.Name);
            }
        }

        return result.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<string>)kvp.Value);
    }

    public async Task<TeamMember> AddSeededMemberAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Instant joinedAt,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.AddSeededMemberAsync(
            teamId, userId, role, joinedAt, cancellationToken));
        InvalidateTeamsCache();
        return result;
    }

    public async Task<bool> PermanentlyDeleteTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.PermanentlyDeleteTeamAsync(teamId, cancellationToken));
        if (result)
            InvalidateTeamsCache();
        return result;
    }

    public Task<IReadOnlyList<TeamOptionDto>> GetBudgetableTeamsAsync(
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetBudgetableTeamsAsync(cancellationToken));

    public Task<IReadOnlyCollection<Guid>> GetEffectiveBudgetCoordinatorTeamIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetEffectiveBudgetCoordinatorTeamIdsAsync(userId, cancellationToken));

    public void RemoveMemberFromAllTeamsCache(Guid userId)
    {
        InvalidateTeamsCache();
    }

    public void InvalidateActiveTeamsCache() => InvalidateTeamsCache();

    private void InvalidateTeamsCache()
    {
        // This API is sync because legacy invalidation callers only expose void.
        // The lock is held for the in-memory clear only except when racing startup
        // warmup; ASP.NET Core has no sync context, and the warm set is small.
        _loadLock.Wait();
        try
        {
            _byTeamId.Clear();
            _isLoaded = false;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task<int> RevokeAllMembershipsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.RevokeAllMembershipsAsync(userId, cancellationToken));
        if (result > 0)
            RemoveMemberFromAllTeamsCache(userId);
        return result;
    }

    public Task<int> GetTotalPendingJoinRequestCountAsync(CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetTotalPendingJoinRequestCountAsync(cancellationToken));

    public Task<IReadOnlyList<Guid>> GetActiveNonSystemTeamCoordinatorUserIdsAsync(
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetActiveNonSystemTeamCoordinatorUserIdsAsync(cancellationToken));

    public Task<IReadOnlyList<TeamMember>> GetActiveMembersForTeamsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetActiveMembersForTeamsAsync(teamIds, cancellationToken));

    public Task<IReadOnlyList<TeamMember>> GetActiveChildMembersByParentIdsAsync(
        IReadOnlyCollection<Guid> parentTeamIds,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetActiveChildMembersByParentIdsAsync(parentTeamIds, cancellationToken));

    public Task<Team?> GetSystemTeamWithActiveMembersAsync(
        SystemTeamType type,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetSystemTeamWithActiveMembersAsync(type, cancellationToken));

    public Task<IReadOnlyList<TeamMember>> GetActiveMembershipsForRoleReconciliationAsync(
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetActiveMembershipsForRoleReconciliationAsync(cancellationToken));

    public async Task<int> ApplyMemberRoleChangesAsync(
        IReadOnlyCollection<(Guid TeamMemberId, TeamMemberRole Role)> changes,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.ApplyMemberRoleChangesAsync(changes, cancellationToken));
        if (result > 0)
            InvalidateTeamsCache();
        return result;
    }

    public Task<IReadOnlyList<Guid>> GetActiveDepartmentCoordinatorUserIdsAsync(
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetActiveDepartmentCoordinatorUserIdsAsync(cancellationToken));

    public Task<bool> IsActiveDepartmentCoordinatorAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.IsActiveDepartmentCoordinatorAsync(userId, cancellationToken));

    public async Task<bool> ApplySystemTeamMembershipDeltaAsync(
        Guid teamId,
        IReadOnlyCollection<Guid> userIdsToAdd,
        IReadOnlyCollection<Guid> userIdsToRemove,
        Instant now,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.ApplySystemTeamMembershipDeltaAsync(
            teamId, userIdsToAdd, userIdsToRemove, now, cancellationToken));
        if (result)
            InvalidateTeamsCache();
        return result;
    }

    public async Task ReassignAsync(
        Guid mergedFromUserId,
        Guid mergedToUserId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct)
    {
        await WithInnerMerge(inner => inner.ReassignAsync(
            mergedFromUserId, mergedToUserId, actorUserId, now, ct));
        InvalidateTeamsCache();
    }

    private async Task<ConcurrentDictionary<Guid, TeamInfo>> GetTeamsByIdAsync(CancellationToken ct)
    {
        if (_isLoaded)
            return _byTeamId;

        await WarmAllAsync(ct);
        return _byTeamId;
    }

    public async Task WarmAllAsync(CancellationToken ct = default)
    {
        if (_isLoaded)
            return;

        await _loadLock.WaitAsync(ct);
        try
        {
            if (_isLoaded)
                return;

            var teams = await _teamRepository.GetAllWithMembersAsync(ct);
            var allUserIds = teams
                .SelectMany(t => t.Members.Where(m => m.LeftAt is null).Select(m => m.UserId))
                .Distinct()
                .ToList();

            await using var scope = _scopeFactory.CreateAsyncScope();
            var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
            var users = allUserIds.Count == 0
                ? new Dictionary<Guid, User>()
                : await userService.GetByIdsWithEmailsAsync(allUserIds, ct);

            _byTeamId.Clear();
            foreach (var team in teams)
                _byTeamId[team.Id] = BuildTeamInfo(team, users);

            _isLoaded = true;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private bool IsUserCoordinatorOfActiveTeam(
        IReadOnlyDictionary<Guid, TeamInfo> teams,
        IReadOnlyCollection<Guid> coordinatorTeamIds,
        Guid teamId,
        Guid userId)
    {
        if (!teams.TryGetValue(teamId, out var team) || !team.IsActive)
        {
            _logger.LogDebug("Coordinator check: team {TeamId} not found in team cache for user {UserId}", teamId, userId);
            return false;
        }

        if (team.Members.Any(m => m.UserId == userId && m.Role == TeamMemberRole.Coordinator))
            return true;

        if (coordinatorTeamIds.Contains(teamId))
            return true;

        return team.ParentTeamId.HasValue
            && IsUserCoordinatorOfActiveTeam(teams, coordinatorTeamIds, team.ParentTeamId.Value, userId);
    }

    private static TeamInfo BuildTeamInfo(Team team, IReadOnlyDictionary<Guid, User> users) => new(
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
        Members: team.Members
            .Where(m => m.LeftAt is null)
            .Select(m =>
            {
                users.TryGetValue(m.UserId, out var u);
                return new TeamMemberInfo(
                    TeamMemberId: m.Id,
                    UserId: m.UserId,
                    DisplayName: u?.DisplayName ?? string.Empty,
                    Email: u?.Email,
                    ProfilePictureUrl: u?.ProfilePictureUrl,
                    Role: m.Role,
                    JoinedAt: m.JoinedAt);
            })
            .ToList(),
        ParentTeamId: team.ParentTeamId);

    private async Task<TResult> WithInner<TResult>(Func<ITeamService, Task<TResult>> action)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ITeamService>(InnerServiceKey);
        return await action(inner);
    }

    private async Task WithInner(Func<ITeamService, Task> action)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ITeamService>(InnerServiceKey);
        await action(inner);
    }

    private async Task WithInnerMerge(Func<IUserMerge, Task> action)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IUserMerge>(InnerServiceKey);
        await action(inner);
    }
}
