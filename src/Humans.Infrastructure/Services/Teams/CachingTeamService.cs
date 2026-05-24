using System.Collections.Concurrent;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Teams;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Services.Teams;

/// <summary>
/// Transparent singleton decorator for <see cref="ITeamService"/> team reads.
/// The scoped inner service owns Teams behavior and writes; this decorator owns the
/// process-local team read model and invalidates it after writes. Inherits
/// <see cref="TrackedCache{TKey, TValue}"/> for hit/miss/invalidation tracking
/// surfaced on /Admin/CacheStats — note that this service serves bulk reads
/// (<see cref="GetTeamsByIdAsync"/>) so per-key hit/miss counts are not
/// recorded; the meaningful diagnostics here are entry count and invalidation
/// count.
/// </summary>
public sealed class CachingTeamService(
    ITeamRepository teamRepository,
    IServiceScopeFactory scopeFactory,
    ILogger<CachingTeamService> logger) : TrackedCache<Guid, TeamInfo>("Team.TeamInfo", warmOnStartup: true, logger),
    ITeamService, IUserMerge
{
    public const string InnerServiceKey = "team-inner";

    /// <summary>
    /// Secondary user→teams index, populated during <see cref="WarmAllAsync"/>
    /// alongside the primary <c>teamId → TeamInfo</c> dict. Serves
    /// <see cref="GetUserTeamsAsync"/> without touching the inner scoped
    /// <see cref="ITeamService"/> / EF.
    ///
    /// <para>Every membership-mutating path on this decorator calls
    /// <see cref="InvalidateTeamsCache"/>, which clears the primary cache and
    /// flips the warmed flag back to false. The next reader drives
    /// <see cref="WarmAllAsync"/> on demand, which empties this index and
    /// rebuilds it from the freshly loaded teams. No per-mutation diffing
    /// against the inverse map is needed — the all-or-nothing re-warm pattern
    /// keeps the two structures consistent by construction.</para>
    ///
    /// <para>Writes during warmup are confined to <see cref="WarmAllAsync"/>,
    /// which the base coalesces under its warm semaphore. Readers go through
    /// <see cref="GetTeamsByIdAsync"/> first (which calls
    /// <see cref="TrackedCache{TKey,TValue}.EnsureWarmedAsync"/>), so a
    /// concurrent reader either sees the post-warmup state or waits for warmup
    /// to complete. The <see cref="HashSet{T}"/> values are written only during
    /// warmup; readers iterate the set without locking.</para>
    /// </summary>
    private readonly ConcurrentDictionary<Guid, HashSet<Guid>> _teamIdsByUserId = new();

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

    public Task<Team?> GetTeamEntityBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetTeamEntityBySlugAsync(slug, cancellationToken));

    public Task<Team?> GetTeamByIdAsync(Guid teamId, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetTeamByIdAsync(teamId, cancellationToken));

    public async Task<TeamInfo?> GetTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        var teamsById = await GetTeamsByIdAsync(cancellationToken);
        return teamsById.GetValueOrDefault(teamId);
    }

    public async Task<TeamInfo?> GetTeamBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        // Cache walk — no repo hit on cache hit. Matches the slug-resolution
        // semantics inner.GetTeamDetailAsync uses (canonical Slug OR CustomSlug).
        var normalizedSlug = slug.ToLowerInvariant();
        var teamsById = await GetTeamsByIdAsync(cancellationToken);
        return teamsById.Values.FirstOrDefault(t =>
            string.Equals(t.Slug, normalizedSlug, StringComparison.Ordinal)
            || (t.CustomSlug is not null && string.Equals(t.CustomSlug, normalizedSlug, StringComparison.Ordinal)));
    }

    public async Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamsAsync(
        CancellationToken cancellationToken = default) =>
        await GetTeamsByIdAsync(cancellationToken);

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
        await using var scope = scopeFactory.CreateAsyncScope();
        var roleAssignmentService = scope.ServiceProvider.GetRequiredService<IRoleAssignmentService>();
        return await TeamDirectoryBuilder.BuildAsync(
            teamsById,
            roleAssignmentService,
            userId,
            cancellationToken);
    }

    public async Task<TeamDetailResult?> GetTeamDetailAsync(
        string slug,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        // Cache-served team detail. Resolves team by slug (or custom slug) from
        // the cached snapshot, walks ChildTeamIds for child links, and stitches
        // members + role definitions from TeamInfo. The repository's
        // GetBySlugWithRelationsAsync + GetRoleDefinitionsAsync — the previous
        // every-render bypass — are NOT called on this path. Pending-request
        // counts (per-user and per-team) remain real-time and route through
        // the inner ITeamService.
        var teamsById = await GetTeamsByIdAsync(cancellationToken);
        var normalizedSlug = slug.ToLowerInvariant();
        var team = teamsById.Values.FirstOrDefault(t =>
            string.Equals(t.Slug, normalizedSlug, StringComparison.Ordinal) ||
            (t.CustomSlug is not null && string.Equals(t.CustomSlug, normalizedSlug, StringComparison.Ordinal)));
        if (team is null)
            return null;

        TeamInfo? parent = team.ParentTeamId.HasValue
            ? teamsById.GetValueOrDefault(team.ParentTeamId.Value)
            : null;

        if (!userId.HasValue)
        {
            var isAnonymouslyVisible = !team.IsHidden && !team.IsSystemTeam
                && ((team.ParentTeamId is null && team.IsPublicPage)
                    || (team.ParentTeamId is not null && team.IsPromotedToDirectory));
            if (!isAnonymouslyVisible)
                return null;

            var coordinators = team.Members
                .Where(m => m.Role == TeamMemberRole.Coordinator)
                .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(MapTeamDetailMemberSummary)
                .ToList();

            return new TeamDetailResult(
                Team: MapTeamSummary(team, parent),
                Members: coordinators,
                ChildTeams: EnumerateChildren(team, teamsById)
                    .Where(c => c.IsActive && c.IsPublicPage && !c.IsHidden)
                    .OrderBy(c => c.Name, StringComparer.Ordinal)
                    .Select(MapTeamLink)
                    .ToList(),
                RoleDefinitions: [],
                IsAuthenticated: false,
                IsCurrentUserMember: false,
                IsCurrentUserCoordinator: false,
                CanCurrentUserJoin: false,
                CanCurrentUserLeave: false,
                CanCurrentUserManage: false,
                CanCurrentUserEditTeam: false,
                CurrentUserPendingRequestId: null,
                PendingRequestCount: 0);
        }

        var currentUserId = userId.Value;
        var isCurrentUserMember = team.Members.Any(m => m.UserId == currentUserId);
        var isCurrentUserCoordinator = IsUserCoordinatorOfActiveTeam(teamsById, team.Id, currentUserId);

        await using var scope = scopeFactory.CreateAsyncScope();
        var roleAssignmentService = scope.ServiceProvider.GetRequiredService<IRoleAssignmentService>();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ITeamService>(InnerServiceKey);

        var isBoardMember = await roleAssignmentService.IsUserBoardMemberAsync(currentUserId, cancellationToken);
        var isAdmin = await roleAssignmentService.IsUserAdminAsync(currentUserId, cancellationToken);
        var isTeamsAdmin = await roleAssignmentService.IsUserTeamsAdminAsync(currentUserId, cancellationToken);
        var canManage = isCurrentUserCoordinator || isBoardMember || isAdmin || isTeamsAdmin;

        if (team.IsHidden && !isBoardMember && !isAdmin && !isTeamsAdmin)
            return null;

        var pendingRequest = await inner.GetUserPendingRequestAsync(team.Id, currentUserId, cancellationToken);
        var pendingRequestCount = canManage
            ? (await inner.GetPendingRequestsForTeamAsync(team.Id, cancellationToken)).Count
            : 0;

        return new TeamDetailResult(
            Team: MapTeamSummary(team, parent),
            Members: team.Members
                .OrderBy(m => m.Role)
                .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(MapTeamDetailMemberSummary)
                .ToList(),
            ChildTeams: EnumerateChildren(team, teamsById)
                .Where(c => c.IsActive)
                .OrderBy(c => c.Name, StringComparer.Ordinal)
                .Select(MapTeamLink)
                .ToList(),
            RoleDefinitions: team.RoleDefinitions ?? [],
            IsAuthenticated: true,
            IsCurrentUserMember: isCurrentUserMember,
            IsCurrentUserCoordinator: isCurrentUserCoordinator,
            CanCurrentUserJoin: !isCurrentUserMember && !team.IsSystemTeam && pendingRequest is null,
            CanCurrentUserLeave: isCurrentUserMember && !team.IsSystemTeam,
            CanCurrentUserManage: canManage,
            CanCurrentUserEditTeam: isBoardMember || isAdmin || isTeamsAdmin,
            CurrentUserPendingRequestId: pendingRequest?.Id,
            PendingRequestCount: pendingRequestCount);
    }

    private static IEnumerable<TeamInfo> EnumerateChildren(
        TeamInfo team,
        IReadOnlyDictionary<Guid, TeamInfo> teamsById)
    {
        if (team.ChildTeamIds is null || team.ChildTeamIds.Count == 0)
            yield break;
        foreach (var childId in team.ChildTeamIds)
        {
            if (teamsById.TryGetValue(childId, out var child))
                yield return child;
        }
    }

    private static TeamPageTeamSummary MapTeamSummary(TeamInfo team, TeamInfo? parent) =>
        TeamPageSummaryMapper.Map(
            id: team.Id,
            name: team.Name,
            parentName: parent?.Name,
            description: team.Description,
            slug: team.Slug,
            isActive: team.IsActive,
            requiresApproval: team.RequiresApproval,
            isSystemTeam: team.IsSystemTeam,
            systemTeamType: team.SystemTeamType,
            createdAt: team.CreatedAt,
            isPublicPage: team.IsPublicPage,
            showCoordinatorsOnPublicPage: team.ShowCoordinatorsOnPublicPage,
            pageContent: team.PageContent,
            callsToAction: team.CallsToAction is null ? [] : [.. team.CallsToAction],
            pageContentUpdatedAt: team.PageContentUpdatedAt,
            pageContentUpdatedByUserId: team.PageContentUpdatedByUserId,
            parentLink: parent is null ? null : new TeamPageTeamLink(parent.Id, parent.Name, parent.Slug));

    private static TeamPageTeamLink MapTeamLink(TeamInfo team) =>
        new(team.Id, team.Name, team.Slug);

    private static TeamDetailMemberSummary MapTeamDetailMemberSummary(TeamMemberInfo member) =>
        new(
            UserId: member.UserId,
            DisplayName: member.DisplayName,
            Email: member.Email,
            ProfilePictureUrl: member.ProfilePictureUrl,
            Role: member.Role,
            JoinedAt: member.JoinedAt);

    public async Task<IReadOnlyList<TeamMember>> GetUserTeamsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Ensure the inverse map and primary dict are in lockstep (both rebuilt
        // by WarmAllAsync). Without warmup the inverse map is empty even when
        // the user is genuinely a member of a team.
        await EnsureWarmedAsync(cancellationToken);

        if (!_teamIdsByUserId.TryGetValue(userId, out var teamIds) || teamIds.Count == 0)
            return [];

        var snapshot = AsReadOnlyDictionary;
        var result = new List<TeamMember>(teamIds.Count);
        foreach (var teamId in teamIds)
        {
            if (!snapshot.TryGetValue(teamId, out var team))
                continue;

            var membership = team.Members.FirstOrDefault(m => m.UserId == userId);
            if (membership is null)
                continue;

            result.Add(ProjectToTeamMember(team, membership, snapshot));
        }

        return result;
    }

    /// <summary>
    /// Synthesize the <see cref="TeamMember"/> Domain entity shape from the
    /// cached <see cref="TeamInfo"/> + <see cref="TeamMemberInfo"/> projections
    /// so consumers of <see cref="GetUserTeamsAsync"/> keep their existing
    /// field access on the membership row and its nested team entity
    /// (SystemTeamType / IsHidden / DisplayName / Slug / Role / JoinedAt /
    /// TeamId / LeftAt) without reaching back to EF. Members are filtered by
    /// <c>LeftAt is null</c> at warmup time, so all synthesized memberships
    /// are active and <see cref="TeamMember.LeftAt"/> stays null.
    ///
    /// <para>Populates the synthesized team's <c>ParentTeam</c> nav when a
    /// parent is in cache so the <c>DisplayName</c> "Parent - Name" form
    /// resolves without an EF round-trip.</para>
    /// </summary>
    private static TeamMember ProjectToTeamMember(
        TeamInfo team,
        TeamMemberInfo membership,
        IReadOnlyDictionary<Guid, TeamInfo> teamsById)
    {
        Team? parent = null;
        if (team.ParentTeamId.HasValue && teamsById.TryGetValue(team.ParentTeamId.Value, out var parentInfo))
        {
            parent = SynthesizeTeam(parentInfo, parent: null);
        }

        var teamEntity = SynthesizeTeam(team, parent);
        return new TeamMember
        {
            Id = membership.TeamMemberId,
            TeamId = team.Id,
            Team = teamEntity,
            UserId = membership.UserId,
            Role = membership.Role,
            JoinedAt = membership.JoinedAt,
            LeftAt = null,
        };
    }

    private static Team SynthesizeTeam(TeamInfo info, Team? parent) => new()
    {
        Id = info.Id,
        Name = info.Name,
        Description = info.Description,
        Slug = info.Slug,
        IsActive = info.IsActive,
        SystemTeamType = info.SystemTeamType,
        RequiresApproval = info.RequiresApproval,
        GoogleGroupPrefix = info.GoogleGroupPrefix,
        CreatedAt = info.CreatedAt,
        UpdatedAt = info.UpdatedAt ?? info.CreatedAt,
        CustomSlug = info.CustomSlug,
        IsPublicPage = info.IsPublicPage,
        ShowCoordinatorsOnPublicPage = info.ShowCoordinatorsOnPublicPage,
        PageContent = info.PageContent,
        PageContentUpdatedAt = info.PageContentUpdatedAt,
        PageContentUpdatedByUserId = info.PageContentUpdatedByUserId,
        CallsToAction = info.CallsToAction is null ? null : [.. info.CallsToAction],
        HasBudget = info.HasBudget,
        IsHidden = info.IsHidden,
        IsSensitive = info.IsSensitive,
        ParentTeamId = info.ParentTeamId,
        ParentTeam = parent,
        IsPromotedToDirectory = info.IsPromotedToDirectory,
    };

    public async Task<IReadOnlyList<MyTeamMembershipSummary>> GetMyTeamMembershipsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Cache-served. Uses the user→teams inverse index from WarmAllAsync to
        // find the user's memberships, and TeamInfo.PendingRequestCount +
        // ChildTeamIds to compute manageable counts. The single remaining inner
        // call is IRoleAssignmentService.IsUserBoardMemberAsync — that data is
        // owned by Auth (not Teams) and is not on TeamInfo.
        await EnsureWarmedAsync(cancellationToken);
        var teamsById = AsReadOnlyDictionary;
        if (!_teamIdsByUserId.TryGetValue(userId, out var teamIds) || teamIds.Count == 0)
            return [];

        await using var scope = scopeFactory.CreateAsyncScope();
        var roleAssignmentService = scope.ServiceProvider.GetRequiredService<IRoleAssignmentService>();
        var isBoardMember = await roleAssignmentService.IsUserBoardMemberAsync(userId, cancellationToken);

        var result = new List<MyTeamMembershipSummary>(teamIds.Count);
        foreach (var teamId in teamIds)
        {
            if (!teamsById.TryGetValue(teamId, out var team))
                continue;

            var membership = team.Members.FirstOrDefault(m => m.UserId == userId);
            if (membership is null)
                continue;

            // Inherited coordinator: a user can manage a team if they're a
            // direct Coordinator, a board member, or coordinate any ancestor
            // team (matches CanUserApproveRequestsForTeamAsync's recursive
            // parent walk via IsUserCoordinatorOfActiveTeam).
            var canManage =
                (isBoardMember || IsUserCoordinatorOfActiveTeam(teamsById, team.Id, userId))
                && !team.IsSystemTeam;

            var pendingCount = 0;
            if (canManage)
            {
                pendingCount += team.PendingRequestCount;
                if (team.ChildTeamIds is not null)
                {
                    foreach (var childId in team.ChildTeamIds)
                    {
                        if (!teamsById.TryGetValue(childId, out var child) || !child.IsActive)
                            continue;
                        pendingCount += child.PendingRequestCount;
                    }
                }
            }

            var displayName = team.ParentTeamId.HasValue
                && teamsById.TryGetValue(team.ParentTeamId.Value, out var parent)
                ? $"{parent.Name} - {team.Name}"
                : team.Name;

            result.Add(new MyTeamMembershipSummary(
                team.Id,
                displayName,
                team.Slug,
                team.IsSystemTeam,
                membership.Role,
                membership.JoinedAt,
                CanLeave: !team.IsSystemTeam,
                PendingRequestCount: pendingCount));
        }

        return result;
    }

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

    public async Task<TeamJoinRequest> RequestToJoinTeamAsync(
        Guid teamId,
        Guid userId,
        string? message,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.RequestToJoinTeamAsync(teamId, userId, message, cancellationToken));
        // Invalidates so the next read of TeamInfo.PendingRequestCount picks up
        // the new pending row.
        InvalidateTeamsCache();
        return result;
    }

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

    public async Task WithdrawJoinRequestAsync(
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.WithdrawJoinRequestAsync(requestId, userId, cancellationToken));
        // Invalidates so the next read of TeamInfo.PendingRequestCount drops the
        // withdrawn pending row.
        InvalidateTeamsCache();
    }

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

    public async Task RejectJoinRequestAsync(
        Guid requestId,
        Guid approverUserId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await WithInner(inner => inner.RejectJoinRequestAsync(requestId, approverUserId, reason, cancellationToken));
        // Invalidates so the next read of TeamInfo.PendingRequestCount drops the
        // rejected pending row.
        InvalidateTeamsCache();
    }

    public Task<IReadOnlyList<TeamJoinRequestSnapshot>> GetPendingRequestsForTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetPendingRequestsForTeamAsync(teamId, cancellationToken));

    public Task<TeamJoinRequestSnapshot?> GetUserPendingRequestAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetUserPendingRequestAsync(teamId, userId, cancellationToken));

    public async Task<bool> IsUserCoordinatorOfTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var teamsById = await GetTeamsByIdAsync(cancellationToken);
        return IsUserCoordinatorOfActiveTeam(teamsById, teamId, userId);
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

    public async Task<IReadOnlyDictionary<Guid, string>> GetManagementRoleNamesByTeamIdsAsync(
        IEnumerable<Guid> teamIds,
        CancellationToken cancellationToken = default)
    {
        // Mirrors TeamRepository.GetPublicManagementRoleNamesByTeamIdsAsync:
        // for each requested team, return the name of its definition flagged
        // both IsManagement and IsPublic. Teams with no such definition are
        // omitted from the result.
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

    public async Task<TeamPageUpdateResult> UpdateTeamPageContentAsync(
        Guid teamId,
        string? pageContent,
        IReadOnlyList<TeamPageCallToActionInput> callsToAction,
        bool isPublicPage,
        bool showCoordinatorsOnPublicPage,
        Guid updatedByUserId,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.UpdateTeamPageContentAsync(
            teamId, pageContent, callsToAction, isPublicPage,
            showCoordinatorsOnPublicPage, updatedByUserId, cancellationToken));
        if (result.Succeeded)
        {
            InvalidateTeamsCache();
        }

        return result;
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
        int? estimatedHours = null,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.CreateRoleDefinitionAsync(
            teamId, name, description, slotCount, priorities, sortOrder,
            period, actorUserId, isPublic, estimatedHours, cancellationToken));
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
        bool canToggleManagement = true,
        int? estimatedHours = null,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.UpdateRoleDefinitionAsync(
            roleDefinitionId, name, description, slotCount, priorities, sortOrder,
            isManagement, period, actorUserId, isPublic, canToggleManagement, estimatedHours, cancellationToken));
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

    public async Task<TeamRoleManagementToggleResult> ToggleRoleIsManagementAsync(
        Guid roleDefinitionId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.ToggleRoleIsManagementAsync(
            roleDefinitionId, actorUserId, cancellationToken));
        InvalidateTeamsCache();
        return result;
    }

    public async Task<IReadOnlyList<TeamRoleDefinitionSnapshot>> GetRoleDefinitionsAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        // Mirrors TeamRepository.GetRoleDefinitionsAsync(teamId): all definitions
        // for the team (no activity / system filter), ordered by SortOrder
        // then Name. Cache projection pre-sorts at warm time.
        var teamsById = await GetTeamsByIdAsync(cancellationToken);
        if (!teamsById.TryGetValue(teamId, out var team) || team.RoleDefinitions is null)
            return [];
        return team.RoleDefinitions;
    }

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

    public async Task<IReadOnlyList<Guid>> GetUserCoordinatedTeamIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Mirrors TeamRepository.GetUserCoordinatorTeamIdsAsync: non-system teams
        // where the user is either an active Coordinator member or holds an
        // active management role assignment.
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

    public async Task<IReadOnlyList<Application.Models.TeamMembership>> GetActiveTeamMembershipsForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var teamsById = await GetTeamsByIdAsync(cancellationToken);
        var rows = new List<Application.Models.TeamMembership>();

        foreach (var team in teamsById.Values.Where(t => t.IsActive))
        {
            if (team.SystemTeamType == SystemTeamType.Volunteers)
                continue;

            var membership = team.Members.FirstOrDefault(m => m.UserId == userId);
            if (membership is not null)
                rows.Add(new Application.Models.TeamMembership(team.Name, membership.Role)
                {
                    IsHidden = team.IsHidden,
                });
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

    public async Task<IReadOnlyCollection<Guid>> GetEffectiveBudgetCoordinatorTeamIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Mirrors TeamService.GetEffectiveBudgetCoordinatorTeamIdsAsync ⇒
        // TeamRepository.GetUserDepartmentCoordinatorTeamIdsAsync +
        // GetActiveChildIdsByParentsAsync. Departments are teams with no parent
        // where the user is either Coordinator or holds a management role
        // assignment (no system-team filter on this path). Children are added
        // when they are active and parented to one of those departments.
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

    public void RemoveMemberFromAllTeamsCache(Guid userId)
    {
        InvalidateTeamsCache();
    }

    public void InvalidateActiveTeamsCache() => InvalidateTeamsCache();

    /// <summary>
    /// Drop the cached team set and let the next read trigger a fresh
    /// <see cref="WarmAllAsync"/>. The base's <see cref="TrackedCache{TKey,TValue}.Clear"/>
    /// flips the warmed flag back to false; <see cref="GetTeamsByIdAsync"/>
    /// observes that and re-warms via
    /// <see cref="TrackedCache{TKey,TValue}.EnsureWarmedAsync"/>.
    /// </summary>
    private void InvalidateTeamsCache() => Clear();

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

    public Task<IReadOnlyList<TeamRoleReconciliationMembership>> GetActiveMembershipsForRoleReconciliationAsync(
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

    private async Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamsByIdAsync(CancellationToken ct)
    {
        await EnsureWarmedAsync(ct);
        return AsReadOnlyDictionary;
    }

    /// <summary>
    /// Bulk-loads every active team's projected <see cref="TeamInfo"/>. Called by
    /// <see cref="TrackedCache{TKey,TValue}.EnsureWarmedAsync"/> at startup and
    /// again on demand after <see cref="InvalidateTeamsCache"/> drops the dict
    /// (post-write re-warm pattern). The base owns concurrency coalescing via
    /// the warm semaphore, so this body is invoked at most once at a time.
    /// </summary>
    protected override async Task WarmAllAsync(CancellationToken ct)
    {
        var teams = await teamRepository.GetAllWithMembersAsync(ct);
        var allUserIds = teams
            .SelectMany(t => t.Members.Where(m => m.LeftAt is null).Select(m => m.UserId))
            .Distinct()
            .ToList();

        await using var scope = scopeFactory.CreateAsyncScope();
        var userService = scope.ServiceProvider.GetRequiredService<IUserServiceRead>();
        var users = allUserIds.Count == 0
            ? new Dictionary<Guid, Application.UserInfo>()
            : await userService.GetUserInfosAsync(allUserIds, ct);
        var managementHolders = await teamRepository.GetActiveManagementRoleHolderUserIdsByTeamAsync(ct);
        var roleDefinitionsByTeam = await teamRepository.GetAllRoleDefinitionsByTeamAsync(ct);

        // Build the child-team index once from the parent FK so each TeamInfo
        // can cheaply enumerate its children without walking the whole map.
        var childIdsByParent = teams
            .Where(t => t.ParentTeamId.HasValue)
            .GroupBy(t => t.ParentTeamId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(t => t.Id).ToList());

        // Bulk pending-request counts so TeamInfo.PendingRequestCount is part of
        // the cache projection. Teams with zero pending requests are absent from
        // the dictionary; BuildTeamInfo defaults to 0 in that case.
        var pendingCounts = await teamRepository.GetPendingCountsByTeamIdsAsync(
            teams.Select(t => t.Id).ToList(), ct);

        // No defensive Clear() — InvalidateTeamsCache already emptied the cache
        // before flipping the warmed flag to false (or the cache is empty on first
        // startup). Set is upsert, so any rare leftover entry is overwritten.
        foreach (var team in teams)
            Set(team.Id, BuildTeamInfo(team, users, managementHolders, roleDefinitionsByTeam, childIdsByParent, pendingCounts));

        // Rebuild the inverse user→teams index from scratch. The base coalesces
        // WarmAllAsync under its warm semaphore so this body runs serially;
        // readers wait at EnsureWarmedAsync until both maps are populated.
        _teamIdsByUserId.Clear();
        foreach (var team in teams)
        {
            foreach (var member in team.Members)
            {
                if (member.LeftAt is not null)
                    continue;

                var set = _teamIdsByUserId.GetOrAdd(member.UserId, static _ => new HashSet<Guid>());
                set.Add(team.Id);
            }
        }
    }

    private bool IsUserCoordinatorOfActiveTeam(
        IReadOnlyDictionary<Guid, TeamInfo> teams,
        Guid teamId,
        Guid userId)
    {
        if (!teams.TryGetValue(teamId, out var team) || !team.IsActive)
        {
            logger.LogDebug("Coordinator check: team {TeamId} not found in team cache for user {UserId}", teamId, userId);
            return false;
        }

        if (team.Members.Any(m => m.UserId == userId && m.Role == TeamMemberRole.Coordinator))
            return true;

        // Mirror GetUserCoordinatorTeamIdsAsync: the "by management role assignment"
        // path was filtered to non-system teams.
        if (!team.IsSystemTeam && team.ManagementRoleHolderUserIds?.Contains(userId) == true)
            return true;

        return team.ParentTeamId.HasValue
            && IsUserCoordinatorOfActiveTeam(teams, team.ParentTeamId.Value, userId);
    }

    private static TeamInfo BuildTeamInfo(
        Team team,
        IReadOnlyDictionary<Guid, Application.UserInfo> users,
        IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> managementHolders,
        IReadOnlyDictionary<Guid, IReadOnlyList<TeamRoleDefinition>> roleDefinitionsByTeam,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> childIdsByParent,
        IReadOnlyDictionary<Guid, int> pendingCounts) => new(
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
                    DisplayName: u?.BurnerName ?? string.Empty,
                    Email: u?.Email,
                    ProfilePictureUrl: u?.ProfilePictureUrl,
                    Role: m.Role,
                    JoinedAt: m.JoinedAt,
                    GoogleEmailStatus: u?.GoogleEmailStatus ?? GoogleEmailStatus.Unknown);
            })
            .ToList(),
        ParentTeamId: team.ParentTeamId,
        GoogleGroupPrefix: team.GoogleGroupPrefix,
        HasBudget: team.HasBudget,
        IsSensitive: team.IsSensitive,
        UpdatedAt: team.UpdatedAt,
        CustomSlug: team.CustomSlug,
        ManagementRoleHolderUserIds: managementHolders.TryGetValue(team.Id, out var holders) ? holders : null,
        RoleDefinitions: roleDefinitionsByTeam.TryGetValue(team.Id, out var defs)
            ? defs.Select(d => ProjectRoleDefinitionSnapshot(d, team)).ToList()
            : null,
        ChildTeamIds: childIdsByParent.TryGetValue(team.Id, out var childIds) ? childIds : null,
        ShowCoordinatorsOnPublicPage: team.ShowCoordinatorsOnPublicPage,
        PageContent: team.PageContent,
        CallsToAction: team.CallsToAction,
        PageContentUpdatedAt: team.PageContentUpdatedAt,
        PageContentUpdatedByUserId: team.PageContentUpdatedByUserId,
        PendingRequestCount: pendingCounts.TryGetValue(team.Id, out var pending) ? pending : 0);

    private static TeamRoleDefinitionSnapshot ProjectRoleDefinitionSnapshot(TeamRoleDefinition d, Team team) =>
        new(
            d.Id,
            d.TeamId,
            team.Name,
            team.Slug,
            d.Name,
            d.Description,
            d.SlotCount,
            d.EstimatedHours,
            d.Priorities,
            d.SortOrder,
            d.IsManagement,
            d.Period,
            d.IsPublic,
            d.Assignments
                .Select(a => new TeamRoleAssignmentSnapshot(
                    a.Id,
                    a.TeamMemberId,
                    a.SlotIndex,
                    a.TeamMember?.UserId))
                .ToList());

    private async Task<TResult> WithInner<TResult>(Func<ITeamService, Task<TResult>> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ITeamService>(InnerServiceKey);
        return await action(inner);
    }

    private async Task WithInner(Func<ITeamService, Task> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ITeamService>(InnerServiceKey);
        await action(inner);
    }

    private async Task WithInnerMerge(Func<IUserMerge, Task> action)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IUserMerge>(InnerServiceKey);
        await action(inner);
    }
}
