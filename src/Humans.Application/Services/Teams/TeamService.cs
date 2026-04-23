using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application;
using Humans.Application.Extensions;
using Humans.Application.Helpers;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;

namespace Humans.Application.Services.Teams;

/// <summary>
/// Application-layer implementation of <see cref="ITeamService"/>, migrated
/// from <c>Humans.Infrastructure.Services.TeamService</c> as §15 Part 1 for
/// the Teams section (issue #540a). Goes through <see cref="ITeamRepository"/>
/// for all owned-table access and through public service interfaces
/// (<see cref="IUserService"/>, <see cref="IRoleAssignmentService"/>,
/// <see cref="IShiftManagementService"/>, <see cref="ITeamResourceService"/>,
/// <see cref="IEmailService"/>, <see cref="ISystemTeamSync"/>) for every
/// cross-section read. Never imports <c>Microsoft.EntityFrameworkCore</c> —
/// structurally enforced by <c>Humans.Application.csproj</c>.
///
/// <para>
/// Owns a private <see cref="ConcurrentDictionary{TKey,TValue}"/> of
/// <see cref="CachedTeam"/> keyed by team id. This mirrors the old
/// <c>CacheKeys.ActiveTeams</c> in-memory projection so team directory /
/// membership / coordinator queries stay synchronous dict reads after the
/// section migration — per §15f, canonical domain data caches use the
/// §15 pattern; <see cref="IMemoryCache"/> is not imported by services in
/// Application. No separate decorator: at ~500-user scale the dict sits in
/// the service itself (same precedent as the non-decorator migrations:
/// User #243, Budget #544, Auth #551). If future profiling shows the
/// dict-miss path is hot, a <c>CachingTeamService</c> decorator can be
/// layered on top without changing this class's signature.
/// </para>
///
/// <para>
/// Cross-section cache invalidation flows through narrow invalidator
/// interfaces (<see cref="INotificationMeterCacheInvalidator"/>,
/// <see cref="IShiftAuthorizationInvalidator"/>) — the same pattern the
/// §15 Profile migration established.
/// </para>
/// </summary>
public sealed class TeamService : ITeamService, IUserDataContributor
{
    private readonly ITeamRepository _repo;
    private readonly IAuditLogService _auditLogService;
    private readonly INotificationEmitter _notificationService;
    private readonly IShiftManagementService _shiftManagementService;
    private readonly INotificationMeterCacheInvalidator _notificationMeterInvalidator;
    private readonly IShiftAuthorizationInvalidator _shiftAuthInvalidator;
    private readonly IServiceProvider _serviceProvider;
    private readonly IMemoryCache _cache;
    private readonly IClock _clock;
    private readonly ILogger<TeamService> _logger;

    // Lazy resolution for services that would form a DI cycle if injected
    // directly. UserService injects ITeamService (for InvalidateActiveTeamsCache),
    // and this service needs IUserService for user-slice stitching — classic
    // circular dependency that we break the same way the pre-migration service
    // did for RoleAssignmentService, EmailService, SystemTeamSync, and
    // TeamResourceService.
    private ITeamResourceService TeamResourceService
        => _serviceProvider.GetRequiredService<ITeamResourceService>();

    private IRoleAssignmentService RoleAssignmentService
        => _serviceProvider.GetRequiredService<IRoleAssignmentService>();

    private IEmailService EmailService
        => _serviceProvider.GetRequiredService<IEmailService>();

    private ISystemTeamSync SystemTeamSync
        => _serviceProvider.GetRequiredService<ISystemTeamSync>();

    private IUserService UserService
        => _serviceProvider.GetRequiredService<IUserService>();

    public TeamService(
        ITeamRepository repo,
        IAuditLogService auditLogService,
        INotificationEmitter notificationService,
        IShiftManagementService shiftManagementService,
        INotificationMeterCacheInvalidator notificationMeterInvalidator,
        IShiftAuthorizationInvalidator shiftAuthInvalidator,
        IServiceProvider serviceProvider,
        IMemoryCache cache,
        IClock clock,
        ILogger<TeamService> logger)
    {
        _repo = repo;
        _auditLogService = auditLogService;
        _notificationService = notificationService;
        _shiftManagementService = shiftManagementService;
        _notificationMeterInvalidator = notificationMeterInvalidator;
        _shiftAuthInvalidator = shiftAuthInvalidator;
        _serviceProvider = serviceProvider;
        _cache = cache;
        _clock = clock;
        _logger = logger;
    }

    // ==========================================================================
    // Create / update
    // ==========================================================================

    public async Task<Team> CreateTeamAsync(
        string name,
        string? description,
        bool requiresApproval,
        Guid? parentTeamId = null,
        string? googleGroupPrefix = null,
        bool isHidden = false,
        CancellationToken cancellationToken = default)
    {
        var baseSlug = SlugHelper.GenerateSlug(name);
        var now = _clock.GetCurrentInstant();

        string[] reservedSlugs = ["roster", "birthdays", "map", "my", "sync", "summary", "create", "search"];
        if (Array.Exists(reservedSlugs, s => string.Equals(baseSlug, s, StringComparison.Ordinal)))
            throw new InvalidOperationException($"The team name '{name}' conflicts with a reserved route");

        if (parentTeamId.HasValue)
        {
            var parent = await _repo.GetByIdAsync(parentTeamId.Value, cancellationToken)
                ?? throw new InvalidOperationException($"Parent team {parentTeamId.Value} not found");

            if (parent.IsSystemTeam)
                throw new InvalidOperationException("System teams cannot be parents");

            if (parent.ParentTeamId.HasValue)
                throw new InvalidOperationException("Cannot nest more than one level — the parent team already has a parent");
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var slug = attempt == 0 ? baseSlug : $"{baseSlug}-{attempt + 1}";

            var collidesWithExistingSlug = await _repo.SlugExistsAsync(slug, excludingTeamId: null, cancellationToken);
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

            var persisted = await _repo.AddTeamWithRequiresApprovalOverrideAsync(team, requiresApproval, cancellationToken);
            if (persisted)
            {
                UpsertCachedTeam(new CachedTeam(team.Id, team.Name, team.Description, team.Slug,
                    team.IsSystemTeam, team.SystemTeamType, team.RequiresApproval, team.IsPublicPage, team.IsHidden,
                    team.IsPromotedToDirectory, team.CreatedAt, [], ParentTeamId: parentTeamId));
                _logger.LogInformation("Created team {TeamName} with slug {Slug}", name, slug);
                return team;
            }

            // Unique-constraint race (slug or custom_slug collided after our pre-check).
            // The repo translated Npgsql 23505 into `false`; retry with the next suffix.
            _logger.LogDebug(
                "Slug collision for '{Slug}' at persist, retrying (attempt {Attempt})",
                slug, attempt + 1);
        }

        throw new InvalidOperationException($"Could not generate unique slug for team '{name}' after 10 attempts");
    }

    public async Task<Team?> GetTeamBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        var normalizedSlug = slug.ToLowerInvariant();
        var team = await _repo.GetBySlugWithRelationsAsync(normalizedSlug, cancellationToken);
        if (team is null)
            return null;

        await StitchMemberUserSlicesAsync(team.Members.Where(m => m.LeftAt is null), cancellationToken);
        return team;
    }

    public async Task<Team?> GetTeamByIdAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        var team = await _repo.GetByIdWithRelationsAsync(teamId, cancellationToken);
        if (team is null)
            return null;

        await StitchMemberUserSlicesAsync(team.Members.Where(m => m.LeftAt is null), cancellationToken);
        return team;
    }

    public Task<IReadOnlyDictionary<Guid, string>> GetTeamNamesByIdsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken = default) =>
        _repo.GetNamesByIdsAsync(teamIds, cancellationToken);

    public Task<IReadOnlyList<Team>> GetAllTeamsAsync(CancellationToken cancellationToken = default) =>
        _repo.GetAllActiveAsync(cancellationToken);

    public Task<IReadOnlyList<Team>> GetUserCreatedTeamsAsync(CancellationToken cancellationToken = default) =>
        _repo.GetAllActiveUserCreatedAsync(cancellationToken);

    public Task<IReadOnlyList<TeamOptionDto>> GetActiveTeamOptionsAsync(CancellationToken cancellationToken = default) =>
        _repo.GetActiveOptionsAsync(cancellationToken);

    public Task<IReadOnlyList<TeamOptionDto>> GetBudgetableTeamsAsync(
        CancellationToken cancellationToken = default) =>
        _repo.GetBudgetableOptionsAsync(cancellationToken);

    public async Task<IReadOnlyCollection<Guid>> GetEffectiveBudgetCoordinatorTeamIdsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var departmentIds = await _repo.GetUserDepartmentCoordinatorTeamIdsAsync(userId, cancellationToken);
        var teamIds = departmentIds.ToHashSet();

        if (teamIds.Count == 0)
            return teamIds;

        var childTeams = await _repo.GetActiveChildIdsByParentsAsync(teamIds.ToList(), cancellationToken);
        foreach (var (childId, _) in childTeams)
            teamIds.Add(childId);

        return teamIds;
    }

    public async Task<(bool Updated, string? PreviousPrefix)> SetGoogleGroupPrefixAsync(
        Guid teamId, string? prefix, CancellationToken cancellationToken = default)
    {
        var (updated, previous) = await _repo.SetGoogleGroupPrefixAsync(teamId, prefix, cancellationToken);
        if (updated)
            InvalidateActiveTeamsCache();
        return (updated, previous);
    }

    public Task<string?> GetTeamNameByGoogleGroupPrefixAsync(
        string prefix, CancellationToken cancellationToken = default) =>
        _repo.GetNameByGoogleGroupPrefixAsync(prefix, cancellationToken);

    public async Task<TeamDirectoryResult> GetTeamDirectoryAsync(
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        var cachedTeams = await GetCachedTeamsAsync(cancellationToken);

        if (!userId.HasValue)
        {
            var publicDepartments = cachedTeams.Values
                .Where(t => !t.IsSystemTeam && !t.IsHidden
                    && ((t.ParentTeamId is null && t.IsPublicPage)
                        || (t.ParentTeamId is not null && t.IsPromotedToDirectory)))
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(t => CreateDirectorySummary(t, cachedTeams, userId))
                .ToList();

            return new TeamDirectoryResult(
                IsAuthenticated: false,
                CanCreateTeam: false,
                MyTeams: [],
                Departments: publicDepartments,
                SystemTeams: []);
        }

        var isBoardMember = await RoleAssignmentService.IsUserBoardMemberAsync(userId.Value, cancellationToken);
        var isAdmin = await RoleAssignmentService.IsUserAdminAsync(userId.Value, cancellationToken);
        var isTeamsAdmin = await RoleAssignmentService.IsUserTeamsAdminAsync(userId.Value, cancellationToken);
        var canCreateTeam = isBoardMember || isAdmin || isTeamsAdmin;
        var canSeeHiddenTeams = canCreateTeam;

        var visibleTeams = canSeeHiddenTeams
            ? cachedTeams.Values
            : cachedTeams.Values.Where(t => !t.IsHidden);

        var directoryTeams = visibleTeams
            .Where(t => t.ParentTeamId is null || t.IsPromotedToDirectory);

        var summaries = directoryTeams
            .Select(t => CreateDirectorySummary(t, cachedTeams, userId))
            .ToList();

        var myTeams = summaries
            .Where(t => t.IsCurrentUserMember)
            .OrderBy(t => t.SortKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var departments = summaries
            .Where(t => !t.IsCurrentUserMember && !t.IsSystemTeam)
            .OrderBy(t => t.SortKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var systemTeams = summaries
            .Where(t => !t.IsCurrentUserMember && t.IsSystemTeam)
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TeamDirectoryResult(
            IsAuthenticated: true,
            CanCreateTeam: canCreateTeam,
            MyTeams: myTeams,
            Departments: departments,
            SystemTeams: systemTeams);
    }

    public async Task<TeamDetailResult?> GetTeamDetailAsync(
        string slug,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        var team = await GetTeamBySlugAsync(slug, cancellationToken);
        if (team is null)
            return null;

        if (!userId.HasValue)
        {
            var isAnonymouslyVisible = !team.IsHidden && !team.IsSystemTeam
                && ((team.ParentTeamId is null && team.IsPublicPage)
                    || (team.ParentTeamId is not null && team.IsPromotedToDirectory));
            if (!isAnonymouslyVisible)
                return null;
        }

        var activeMembers = team.Members
            .Where(m => m.LeftAt is null)
            .ToList();

        if (!userId.HasValue)
        {
            var coordinators = activeMembers
                .Where(m => m.Role == TeamMemberRole.Coordinator)
#pragma warning disable CS0618 // Cross-domain nav populated in-memory via StitchMemberUserSlicesAsync
                .OrderBy(m => m.User.DisplayName, StringComparer.OrdinalIgnoreCase)
#pragma warning restore CS0618
                .Select(MapTeamDetailMemberSummary)
                .ToList();

            return new TeamDetailResult(
                Team: team,
                Members: coordinators,
                ChildTeams: team.ChildTeams
                    .Where(c => c.IsActive && c.IsPublicPage && !c.IsHidden)
                    .OrderBy(c => c.Name, StringComparer.Ordinal)
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
        var isCurrentUserMember = activeMembers.Any(m => m.UserId == currentUserId);
        var isCurrentUserCoordinator = await IsUserCoordinatorOfTeamAsync(team.Id, currentUserId, cancellationToken);
        var isBoardMember = await RoleAssignmentService.IsUserBoardMemberAsync(currentUserId, cancellationToken);
        var isAdmin = await RoleAssignmentService.IsUserAdminAsync(currentUserId, cancellationToken);
        var isTeamsAdmin = await RoleAssignmentService.IsUserTeamsAdminAsync(currentUserId, cancellationToken);
        var canManage = isCurrentUserCoordinator || isBoardMember || isAdmin || isTeamsAdmin;

        if (team.IsHidden && !isBoardMember && !isAdmin && !isTeamsAdmin)
            return null;

        var pendingRequest = await GetUserPendingRequestAsync(team.Id, currentUserId, cancellationToken);
        var pendingRequestCount = canManage
            ? (await GetPendingRequestsForTeamAsync(team.Id, cancellationToken)).Count
            : 0;
        var roleDefinitions = await GetRoleDefinitionsAsync(team.Id, cancellationToken);
        await StitchRoleAssignmentUserSlicesAsync(roleDefinitions, cancellationToken);

        return new TeamDetailResult(
            Team: team,
            Members: activeMembers
                .OrderBy(m => m.Role)
#pragma warning disable CS0618 // Cross-domain nav populated in-memory via StitchMemberUserSlicesAsync
                .ThenBy(m => m.User.DisplayName, StringComparer.OrdinalIgnoreCase)
#pragma warning restore CS0618
                .Select(MapTeamDetailMemberSummary)
                .ToList(),
            ChildTeams: team.ChildTeams
                .Where(c => c.IsActive)
                .OrderBy(c => c.Name, StringComparer.Ordinal)
                .ToList(),
            RoleDefinitions: roleDefinitions,
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

    public async Task<IReadOnlyList<TeamMember>> GetUserTeamsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var memberships = await _repo.GetActiveByUserIdAsync(userId, cancellationToken);
        await StitchMemberUserSlicesAsync(memberships, cancellationToken);
        return memberships;
    }

    public async Task<IReadOnlyList<MyTeamMembershipSummary>> GetMyTeamMembershipsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var memberships = await GetUserTeamsAsync(userId, cancellationToken);
        var isBoardMember = await RoleAssignmentService.IsUserBoardMemberAsync(userId, cancellationToken);

        var coordinatorTeamIds = memberships
            .Where(m => (m.Role == TeamMemberRole.Coordinator || isBoardMember) && !m.Team.IsSystemTeam)
            .Select(m => m.TeamId)
            .ToHashSet();

        var childTeamsByParent = coordinatorTeamIds.Count > 0
            ? await _repo.GetActiveChildIdsByParentsAsync(coordinatorTeamIds.ToList(), cancellationToken)
            : [];

        var allManageableTeamIds = coordinatorTeamIds
            .Union(childTeamsByParent.Select(c => c.ChildId))
            .ToList();

        var pendingCounts = allManageableTeamIds.Count > 0
            ? await _repo.GetPendingCountsByTeamIdsAsync(allManageableTeamIds, cancellationToken)
            : new Dictionary<Guid, int>();

        return memberships
            .Select(m =>
            {
                var directCount = pendingCounts.GetValueOrDefault(m.TeamId, 0);

                var childCount = coordinatorTeamIds.Contains(m.TeamId)
                    ? childTeamsByParent
                        .Where(c => c.ParentId == m.TeamId)
                        .Sum(c => pendingCounts.GetValueOrDefault(c.ChildId, 0))
                    : 0;

                return new MyTeamMembershipSummary(
                    m.TeamId,
                    m.Team.DisplayName,
                    m.Team.Slug,
                    m.Team.IsSystemTeam,
                    m.Role,
                    m.JoinedAt,
                    CanLeave: !m.Team.IsSystemTeam,
                    PendingRequestCount: directCount + childCount);
            })
            .ToList();
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
        var team = await _repo.FindForMutationAsync(teamId, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
        {
            team.Description = description;
            team.GoogleGroupPrefix = googleGroupPrefix;
            team.UpdatedAt = _clock.GetCurrentInstant();
            await _repo.UpdateTeamAsync(team, cancellationToken);
            InvalidateActiveTeamsCache();
            return team;
        }

        if (parentTeamId.HasValue)
        {
            if (parentTeamId.Value == teamId)
                throw new InvalidOperationException("A team cannot be its own parent");

            var hasChildren = await _repo.HasActiveChildrenAsync(teamId, cancellationToken);
            if (hasChildren)
                throw new InvalidOperationException("This team has sub-teams and cannot become a child of another team");

            var parent = await _repo.GetByIdAsync(parentTeamId.Value, cancellationToken)
                ?? throw new InvalidOperationException($"Parent team {parentTeamId.Value} not found");

            if (parent.IsSystemTeam)
                throw new InvalidOperationException("System teams cannot be parents");

            if (parent.ParentTeamId.HasValue)
                throw new InvalidOperationException("Cannot nest more than one level — the parent team already has a parent");
        }

        if (!string.IsNullOrWhiteSpace(customSlug))
        {
            var normalized = SlugHelper.GenerateSlug(customSlug);
            if (string.IsNullOrEmpty(normalized))
                throw new InvalidOperationException("Custom slug is not valid. Use lowercase letters, numbers, and hyphens.");

            var customSlugTaken = await _repo.SlugExistsAsync(normalized, excludingTeamId: teamId, cancellationToken);
            if (customSlugTaken)
                throw new InvalidOperationException($"The slug '{normalized}' is already in use by another team.");

            customSlug = normalized;
        }
        else
        {
            customSlug = null;
        }

        var becomingChild = parentTeamId.HasValue && !team.ParentTeamId.HasValue;
        var parentChanging = parentTeamId != team.ParentTeamId;
        var usersNeedingShiftAuthorizationInvalidation = parentChanging
            ? await _repo.GetUserIdsWithManagementOnTeamAsync(teamId, cancellationToken)
            : (IReadOnlyList<Guid>)[];

        if (!string.Equals(team.Name, name, StringComparison.Ordinal))
        {
            var newSlug = SlugHelper.GenerateSlug(name);
            var slugTaken = await _repo.SlugExistsAsync(newSlug, excludingTeamId: teamId, cancellationToken);
            if (!slugTaken)
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
        if (isSensitive.HasValue)
            team.IsSensitive = isSensitive.Value;
        if (isPromotedToDirectory.HasValue)
            team.IsPromotedToDirectory = isPromotedToDirectory.Value;
        if (team.IsSystemTeam || parentTeamId.HasValue)
        {
            team.IsPublicPage = false;
            team.ShowCoordinatorsOnPublicPage = false;
        }
        team.UpdatedAt = _clock.GetCurrentInstant();

        await _repo.UpdateTeamAsync(team, cancellationToken);

        InvalidateActiveTeamsCache();
        InvalidateShiftAuthorization(usersNeedingShiftAuthorizationInvalidation);

        if (becomingChild && usersNeedingShiftAuthorizationInvalidation.Count > 0)
        {
            foreach (var userId in usersNeedingShiftAuthorizationInvalidation)
                await SystemTeamSync.SyncCoordinatorsMembershipForUserAsync(userId, cancellationToken);
        }

        _logger.LogInformation("Updated team {TeamId} ({TeamName})", teamId, name);

        return team;
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
        var team = await _repo.FindForMutationAsync(teamId, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (callsToAction.Count > 3)
            throw new InvalidOperationException("A team can have at most 3 calls to action.");

        if (callsToAction.Count(c => c.Style == CallToActionStyle.Primary) > 1)
            throw new InvalidOperationException("Only one primary call to action is allowed.");

        var canBePublic = !team.IsSystemTeam && !team.ParentTeamId.HasValue;

        if (isPublicPage && !canBePublic)
            throw new InvalidOperationException("Only departments (non-system, top-level teams) can be made public.");

        var normalizedShowCoordinatorsOnPublicPage =
            canBePublic && isPublicPage && showCoordinatorsOnPublicPage;

        var now = _clock.GetCurrentInstant();
        team.PageContent = pageContent;
        team.CallsToAction = callsToAction;
        team.IsPublicPage = isPublicPage;
        team.ShowCoordinatorsOnPublicPage = normalizedShowCoordinatorsOnPublicPage;
        team.PageContentUpdatedAt = now;
        team.PageContentUpdatedByUserId = updatedByUserId;
        team.UpdatedAt = now;

        await _repo.UpdateTeamAsync(team, cancellationToken);

        await _auditLogService.LogAsync(
            AuditAction.TeamPageContentUpdated, nameof(Team), teamId,
            $"Team page content updated. Public: {isPublicPage}",
            updatedByUserId);

        TryUpdateCachedTeam(teamId, cachedTeam => cachedTeam with { IsPublicPage = isPublicPage });
    }

    public async Task DeleteTeamAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        var team = await _repo.GetByIdAsync(teamId, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
            throw new InvalidOperationException("Cannot delete system team");

        var hasActiveChildren = await _repo.HasActiveChildrenAsync(teamId, cancellationToken);
        if (hasActiveChildren)
            throw new InvalidOperationException("Cannot deactivate a team that has active sub-teams. Remove or reassign sub-teams first.");

        var now = _clock.GetCurrentInstant();

        // Close out all active memberships + mark team inactive in one transaction.
        var closedCount = await _repo.DeactivateTeamAsync(teamId, now, cancellationToken);

        // NOTE: GoogleResource.IsActive stays true here. The next Google reconciliation
        // tick sees Team.IsActive == false with every TeamMember.LeftAt set, computes
        // every current Google permission as an Extra, revokes them, and then flips
        // GoogleResource.IsActive to false via the owning service.

        RemoveCachedTeam(teamId);

        _logger.LogInformation(
            "Deactivated team {TeamId} ({TeamName}); closed {MemberCount} memberships",
            teamId, team.Name, closedCount);
    }

    // ==========================================================================
    // Membership — join / leave / withdraw / approve / reject
    // ==========================================================================

    public async Task<TeamJoinRequest> RequestToJoinTeamAsync(
        Guid teamId,
        Guid userId,
        string? message,
        CancellationToken cancellationToken = default)
    {
        var team = await _repo.GetByIdAsync(teamId, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
            throw new InvalidOperationException("Cannot request to join system team");

        if (team.IsHidden)
            throw new InvalidOperationException("Cannot request to join a hidden team");

        if (!team.RequiresApproval)
            throw new InvalidOperationException("This team does not require approval. Use JoinTeamDirectlyAsync instead.");

        var existingRequest = await _repo.FindUserPendingRequestAsync(teamId, userId, cancellationToken);
        if (existingRequest is not null)
            throw new InvalidOperationException("User already has a pending request for this team");

        var isMember = await IsUserMemberOfTeamAsync(teamId, userId, cancellationToken);
        if (isMember)
            throw new InvalidOperationException("User is already a member of this team");

        var request = new TeamJoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Message = message,
            RequestedAt = _clock.GetCurrentInstant()
        };

        await _repo.AddRequestAsync(request, cancellationToken);

        _logger.LogInformation("User {UserId} requested to join team {TeamId}", userId, teamId);

        return request;
    }

    public async Task<TeamMember> JoinTeamDirectlyAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var team = await _repo.GetByIdAsync(teamId, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
            throw new InvalidOperationException("Cannot directly join system team");

        if (team.IsHidden)
            throw new InvalidOperationException("Cannot directly join a hidden team");

        if (team.RequiresApproval)
            throw new InvalidOperationException("This team requires approval. Use RequestToJoinTeamAsync instead.");

        var existingMember = await _repo.IsActiveMemberAsync(teamId, userId, cancellationToken);
        if (existingMember)
            throw new InvalidOperationException("User is already a member of this team");

        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Role = TeamMemberRole.Member,
            JoinedAt = _clock.GetCurrentInstant()
        };

        var outboxEvent = await BuildOutboxEventAsync(
            member.Id, teamId, userId, GoogleSyncOutboxEventTypes.AddUserToTeamResources, cancellationToken);

        bool success;
        try
        {
            success = outboxEvent is null
                ? await TryAddMemberOnlyAsync(member, cancellationToken)
                : await _repo.AddMemberWithOutboxAsync(member, outboxEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add user {UserId} to team {TeamId} directly", userId, teamId);
            throw;
        }

        if (!success)
            throw new InvalidOperationException("User is already a member of this team");

        await _auditLogService.LogAsync(
            AuditAction.TeamJoinedDirectly, nameof(Team), teamId,
            $"Joined {team.Name} directly",
            userId,
            relatedEntityId: userId, relatedEntityType: nameof(User));

        var joinedUser = await UserService.GetByIdAsync(userId, cancellationToken);
        if (joinedUser is not null)
        {
            AddMemberToTeamCache(teamId, new CachedTeamMember(
                member.Id, userId, joinedUser.DisplayName, joinedUser.ProfilePictureUrl,
                TeamMemberRole.Member, member.JoinedAt));
        }

        await SendAddedToTeamEmailAsync(userId, team, cancellationToken);

        return member;
    }

    private async Task<bool> TryAddMemberOnlyAsync(TeamMember member, CancellationToken ct)
    {
        // Pre-check serves as the primary guard; the DB unique constraint
        // is a secondary safety net. At single-server ~500-user scale the
        // race window is negligible, but we still bubble repository errors
        // up as InvalidOperationException so callers get the same duplicate
        // message regardless of which layer detected the conflict.
        await _repo.AddMemberAsync(member, ct);
        return true;
    }

    public async Task<bool> LeaveTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var team = await _repo.GetByIdAsync(teamId, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
            throw new InvalidOperationException("Cannot leave system team manually");

        var member = await _repo.FindActiveMemberForMutationAsync(teamId, userId, cancellationToken);
        if (member is null)
            throw new InvalidOperationException("User is not a member of this team");

        var wasCoordinator = member.Role == TeamMemberRole.Coordinator;

        var outboxEvent = await BuildOutboxEventAsync(
            member.Id, teamId, userId, GoogleSyncOutboxEventTypes.RemoveUserFromTeamResources, cancellationToken);

        var now = _clock.GetCurrentInstant();
        var removedAssignments = await _repo.MarkMemberLeftWithOutboxAsync(
            member.Id, now, outboxEvent, cancellationToken);

        await _auditLogService.LogAsync(
            AuditAction.TeamLeft, nameof(Team), teamId,
            $"Left {team.Name}",
            userId,
            relatedEntityId: userId, relatedEntityType: nameof(User));

        RemoveMemberFromTeamCache(teamId, userId);
        InvalidateShiftAuthorizationIfNeeded(userId, removedAssignments);

        return wasCoordinator;
    }

    public async Task WithdrawJoinRequestAsync(
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var withdrew = await _repo.WithdrawRequestAsync(requestId, userId, now, cancellationToken);
        if (!withdrew)
            throw new InvalidOperationException("Join request not found");

        _notificationMeterInvalidator.Invalidate();

        _logger.LogInformation("User {UserId} withdrew join request {RequestId}", userId, requestId);
    }

    public async Task<TeamMember> ApproveJoinRequestAsync(
        Guid requestId,
        Guid approverUserId,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        var request = await _repo.FindRequestWithTeamForMutationAsync(requestId, cancellationToken)
            ?? throw new InvalidOperationException("Join request not found");

        var canApprove = await CanUserApproveRequestsForTeamAsync(request.TeamId, approverUserId, cancellationToken);
        if (!canApprove)
            throw new InvalidOperationException("User does not have permission to approve requests for this team");

        // Mutate scalar fields that will be persisted inside the bundled repo call.
        if (request.Status != TeamJoinRequestStatus.Pending)
            throw new InvalidOperationException("Can only approve pending requests");
        request.Status = TeamJoinRequestStatus.Approved;
        request.ReviewedByUserId = approverUserId;
        request.ReviewNotes = notes;
        var now = _clock.GetCurrentInstant();
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
            success = await _repo.ApproveRequestWithMemberAsync(request, member, outboxEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to approve join request {RequestId} for user {UserId} to team {TeamId}",
                requestId, request.UserId, request.TeamId);
            throw;
        }

        if (!success)
            throw new InvalidOperationException("User is already a member of this team");

        await _auditLogService.LogAsync(
            AuditAction.TeamJoinRequestApproved, nameof(Team), request.TeamId,
            $"Join request for {request.Team.Name} approved",
            approverUserId,
            relatedEntityId: request.UserId, relatedEntityType: nameof(User));

        _notificationMeterInvalidator.Invalidate();
        var joinedUser = await UserService.GetByIdAsync(request.UserId, cancellationToken);
        if (joinedUser is not null)
        {
            AddMemberToTeamCache(request.TeamId, new CachedTeamMember(
                member.Id, request.UserId, joinedUser.DisplayName, joinedUser.ProfilePictureUrl,
                TeamMemberRole.Member, member.JoinedAt));
        }

        await SendAddedToTeamEmailAsync(request.UserId, request.Team, cancellationToken);

        return member;
    }

    public async Task RejectJoinRequestAsync(
        Guid requestId,
        Guid approverUserId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var request = await _repo.FindRequestForMutationAsync(requestId, cancellationToken)
            ?? throw new InvalidOperationException("Join request not found");

        var canApprove = await CanUserApproveRequestsForTeamAsync(request.TeamId, approverUserId, cancellationToken);
        if (!canApprove)
            throw new InvalidOperationException("User does not have permission to reject requests for this team");

        var now = _clock.GetCurrentInstant();
        var rejected = await _repo.RejectRequestAsync(requestId, approverUserId, reason, now, cancellationToken);
        if (!rejected)
            throw new InvalidOperationException("Can only reject pending requests");

        await _auditLogService.LogAsync(
            AuditAction.TeamJoinRequestRejected, nameof(Team), request.TeamId,
            $"Join request for team rejected: {reason}",
            approverUserId,
            relatedEntityId: request.UserId, relatedEntityType: nameof(User));

        _notificationMeterInvalidator.Invalidate();
    }

    public async Task<IReadOnlyList<TeamJoinRequest>> GetPendingRequestsForApproverAsync(
        Guid approverUserId,
        CancellationToken cancellationToken = default)
    {
        var isBoardMember = await RoleAssignmentService.IsUserBoardMemberAsync(approverUserId, cancellationToken);
        var isTeamsAdmin = !isBoardMember && await RoleAssignmentService.IsUserTeamsAdminAsync(approverUserId, cancellationToken);

        var directLeadTeamIds = await _repo.GetUserCoordinatorTeamIdsAsync(approverUserId, cancellationToken);

        var childTeams = directLeadTeamIds.Count > 0
            ? await _repo.GetActiveChildIdsByParentsAsync(directLeadTeamIds, cancellationToken)
            : [];

        var allLeadTeamIds = directLeadTeamIds.Union(childTeams.Select(c => c.ChildId)).ToList();

        IReadOnlyList<TeamJoinRequest> requests;
        if (isBoardMember || isTeamsAdmin)
            requests = await _repo.GetAllPendingWithTeamsAsync(cancellationToken);
        else if (allLeadTeamIds.Count > 0)
            requests = await _repo.GetPendingForTeamIdsAsync(allLeadTeamIds, cancellationToken);
        else
            return [];

        // Stitch the cross-domain User slice in memory.
        await StitchJoinRequestUserSlicesAsync(requests, cancellationToken);

        return requests;
    }

    public async Task<IReadOnlyList<TeamJoinRequest>> GetPendingRequestsForTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        var requests = await _repo.GetPendingForTeamAsync(teamId, cancellationToken);
        await StitchJoinRequestUserSlicesAsync(requests, cancellationToken);
        return requests;
    }

    public Task<TeamJoinRequest?> GetUserPendingRequestAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default) =>
        _repo.FindUserPendingRequestAsync(teamId, userId, cancellationToken);

    public async Task<bool> CanUserApproveRequestsForTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var isAdmin = await RoleAssignmentService.IsUserAdminAsync(userId, cancellationToken);
        if (isAdmin) return true;

        var isBoardMember = await RoleAssignmentService.IsUserBoardMemberAsync(userId, cancellationToken);
        if (isBoardMember) return true;

        var isTeamsAdmin = await RoleAssignmentService.IsUserTeamsAdminAsync(userId, cancellationToken);
        if (isTeamsAdmin) return true;

        return await IsUserCoordinatorOfTeamAsync(teamId, userId, cancellationToken);
    }

    public async Task<bool> IsUserMemberOfTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var cached = await GetCachedTeamsAsync(cancellationToken);
        return cached.TryGetValue(teamId, out var team) && team.Members.Any(m => m.UserId == userId);
    }

    public async Task<bool> IsUserCoordinatorOfTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var cached = await GetCachedTeamsAsync(cancellationToken);
        if (!cached.TryGetValue(teamId, out var team))
        {
            _logger.LogDebug("Coordinator check: team {TeamId} not found in cache for user {UserId}", teamId, userId);
            return false;
        }

        if (team.Members.Any(m => m.UserId == userId && m.Role == TeamMemberRole.Coordinator))
        {
            _logger.LogDebug("Coordinator check: user {UserId} is direct coordinator of team {TeamName} ({TeamId})",
                userId, team.Name, teamId);
            return true;
        }

        var coordinatorTeamIds = await _repo.GetUserCoordinatorTeamIdsAsync(userId, cancellationToken);
        if (coordinatorTeamIds.Contains(teamId))
        {
            _logger.LogDebug("Coordinator check: user {UserId} has IsManagement role on team {TeamName} ({TeamId})",
                userId, team.Name, teamId);
            return true;
        }

        if (team.ParentTeamId.HasValue)
        {
            _logger.LogDebug("Coordinator check: checking parent team {ParentTeamId} for user {UserId} on team {TeamName} ({TeamId})",
                team.ParentTeamId.Value, userId, team.Name, teamId);
            return await IsUserCoordinatorOfTeamAsync(team.ParentTeamId.Value, userId, cancellationToken);
        }

        _logger.LogDebug("Coordinator check: user {UserId} is NOT coordinator of team {TeamName} ({TeamId})",
            userId, team.Name, teamId);
        return false;
    }

    public async Task<bool> RemoveMemberAsync(
        Guid teamId,
        Guid userId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var team = await _repo.GetByIdAsync(teamId, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
            throw new InvalidOperationException("Cannot remove members from system team manually");

        var canApprove = await CanUserApproveRequestsForTeamAsync(teamId, actorUserId, cancellationToken);
        if (!canApprove)
            throw new InvalidOperationException("User does not have permission to remove members from this team");

        var member = await _repo.FindActiveMemberForMutationAsync(teamId, userId, cancellationToken)
            ?? throw new InvalidOperationException("User is not a member of this team");

        var wasCoordinator = member.Role == TeamMemberRole.Coordinator;

        var outboxEvent = await BuildOutboxEventAsync(
            member.Id, teamId, userId, GoogleSyncOutboxEventTypes.RemoveUserFromTeamResources, cancellationToken);

        var now = _clock.GetCurrentInstant();
        var removedAssignments = await _repo.MarkMemberLeftWithOutboxAsync(
            member.Id, now, outboxEvent, cancellationToken);

        await _auditLogService.LogAsync(
            AuditAction.TeamMemberRemoved, nameof(Team), teamId,
            $"Member removed from {team.Name}",
            actorUserId,
            relatedEntityId: userId, relatedEntityType: nameof(User));

        RemoveMemberFromTeamCache(teamId, userId);
        InvalidateShiftAuthorizationIfNeeded(userId, removedAssignments);

        try
        {
            await _notificationService.SendAsync(
                NotificationSource.TeamMemberRemoved,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                $"You were removed from {team.Name}",
                [userId],
                actionUrl: "/Teams",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch TeamMemberRemoved notification for user {UserId} team {TeamId}", userId, teamId);
        }

        return wasCoordinator;
    }

    public async Task<TeamMember> AddMemberToTeamAsync(
        Guid teamId,
        Guid targetUserId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var team = await _repo.GetByIdAsync(teamId, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
            throw new InvalidOperationException("Cannot add members to system teams manually");

        var isExisting = await _repo.IsActiveMemberAsync(teamId, targetUserId, cancellationToken);
        if (isExisting)
            throw new InvalidOperationException("User is already a member of this team");

        // Resolve any pending join request BEFORE adding (single transaction via the
        // approve-with-member path when a pending request exists). If no request is
        // pending, fall back to AddMemberWithOutboxAsync.
        var pendingRequest = await _repo.FindUserPendingRequestAsync(teamId, targetUserId, cancellationToken);
        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = targetUserId,
            Role = TeamMemberRole.Member,
            JoinedAt = _clock.GetCurrentInstant()
        };

        var outboxEvent = await BuildOutboxEventAsync(
            member.Id, teamId, targetUserId, GoogleSyncOutboxEventTypes.AddUserToTeamResources, cancellationToken);

        bool success;
        try
        {
            if (pendingRequest is not null)
            {
                // The pending request was returned detached (AsNoTracking). Approve
                // via the full request-path so state history and reviewer fields land.
                if (pendingRequest.Status == TeamJoinRequestStatus.Pending)
                {
                    pendingRequest.Status = TeamJoinRequestStatus.Approved;
                    pendingRequest.ReviewedByUserId = actorUserId;
                    pendingRequest.ReviewNotes = "Added directly by team manager";
                    pendingRequest.ResolvedAt = _clock.GetCurrentInstant();
                }
                // Re-fetch by id for mutation tracking in the compound path.
                var tracked = await _repo.FindRequestForMutationAsync(pendingRequest.Id, cancellationToken);
                if (tracked is not null)
                {
                    tracked.Status = pendingRequest.Status;
                    tracked.ReviewedByUserId = pendingRequest.ReviewedByUserId;
                    tracked.ReviewNotes = pendingRequest.ReviewNotes;
                    tracked.ResolvedAt = pendingRequest.ResolvedAt;
                    success = await _repo.ApproveRequestWithMemberAsync(tracked, member, outboxEvent, cancellationToken);
                }
                else
                {
                    success = outboxEvent is null
                        ? await TryAddMemberOnlyAsync(member, cancellationToken)
                        : await _repo.AddMemberWithOutboxAsync(member, outboxEvent, cancellationToken);
                }
            }
            else
            {
                success = outboxEvent is null
                    ? await TryAddMemberOnlyAsync(member, cancellationToken)
                    : await _repo.AddMemberWithOutboxAsync(member, outboxEvent, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add user {UserId} to team {TeamId}", targetUserId, teamId);
            throw;
        }

        if (!success)
            throw new InvalidOperationException("User is already a member of this team");

        await _auditLogService.LogAsync(
            AuditAction.TeamMemberAdded, nameof(Team), teamId,
            $"Member added to {team.Name}",
            actorUserId,
            relatedEntityId: targetUserId, relatedEntityType: nameof(User));

        var addedUser = await UserService.GetByIdAsync(targetUserId, cancellationToken);
        if (addedUser is not null)
        {
            AddMemberToTeamCache(teamId, new CachedTeamMember(
                member.Id, targetUserId, addedUser.DisplayName, addedUser.ProfilePictureUrl,
                TeamMemberRole.Member, member.JoinedAt));
        }

        await SendAddedToTeamEmailAsync(targetUserId, team, cancellationToken);

        return member;
    }

    public async Task SetMemberRoleAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var newRole = await _repo.SetMemberRoleAsync(teamId, userId, role, cancellationToken);
        if (newRole is null)
            return;

        UpdateMemberRoleInTeamCache(teamId, userId, newRole.Value);

        await _auditLogService.LogAsync(
            AuditAction.TeamMemberRoleChanged, nameof(Team), teamId,
            $"Set member role to {role}",
            actorUserId,
            relatedEntityId: userId, relatedEntityType: nameof(User));
    }

    public async Task<IReadOnlyList<TeamMember>> GetTeamMembersAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        var members = await _repo.GetActiveMembersAsync(teamId, cancellationToken);
        await StitchMemberUserSlicesAsync(members, cancellationToken);
        return members
            .OrderBy(m => m.Role)
#pragma warning disable CS0618 // Populated in-memory by StitchMemberUserSlicesAsync
            .ThenBy(m => m.User.DisplayName, StringComparer.OrdinalIgnoreCase)
#pragma warning restore CS0618
            .ToList();
    }

    public Task<IReadOnlyList<Guid>> GetActiveMemberUserIdsAsync(
        Guid teamId,
        CancellationToken cancellationToken = default) =>
        _repo.GetActiveMemberUserIdsAsync(teamId, cancellationToken);

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetActiveNonSystemTeamNamesByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default) =>
        _repo.GetActiveNonSystemTeamNamesByUserIdsAsync(userIds, cancellationToken);

    // ==========================================================================
    // Team Role Definitions
    // ==========================================================================

    public async Task<TeamRoleDefinition> CreateRoleDefinitionAsync(
        Guid teamId, string name, string? description, int slotCount,
        List<SlotPriority> priorities, int sortOrder, RolePeriod period, Guid actorUserId,
        bool isPublic = true,
        CancellationToken cancellationToken = default)
    {
        var team = await _repo.GetByIdAsync(teamId, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
            throw new InvalidOperationException("Cannot add role definitions to system teams");

        if (slotCount < 1)
            throw new InvalidOperationException("Slot count must be at least 1");

        if (priorities.Count != slotCount)
            throw new InvalidOperationException($"Priorities count ({priorities.Count}) must match slot count ({slotCount})");

        ValidateRoleName(name);

        var lowerName = name.ToLowerInvariant();
        var nameExists = await _repo.RoleDefinitionNameExistsAsync(teamId, lowerName, excludingId: null, cancellationToken);
        if (nameExists)
            throw new InvalidOperationException($"A role definition with name '{name}' already exists for this team");

        var canManage = await CanUserApproveRequestsForTeamAsync(teamId, actorUserId, cancellationToken);
        if (!canManage)
            throw new InvalidOperationException("User does not have permission to manage role definitions for this team");

        var now = _clock.GetCurrentInstant();
        var definition = new TeamRoleDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Name = name,
            Description = description,
            SlotCount = slotCount,
            Priorities = priorities,
            SortOrder = sortOrder,
            IsPublic = isPublic,
            Period = period,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _repo.AddRoleDefinitionAsync(definition, cancellationToken);

        await _auditLogService.LogAsync(
            AuditAction.TeamRoleDefinitionCreated, nameof(TeamRoleDefinition), definition.Id,
            $"Role definition '{name}' created for team {team.Name}",
            actorUserId,
            relatedEntityId: teamId, relatedEntityType: nameof(Team));

        return definition;
    }

    public async Task<TeamRoleDefinition> UpdateRoleDefinitionAsync(
        Guid roleDefinitionId, string name, string? description, int slotCount,
        List<SlotPriority> priorities, int sortOrder, bool isManagement, RolePeriod period, Guid actorUserId,
        bool isPublic = true,
        CancellationToken cancellationToken = default)
    {
        var definition = await _repo.FindRoleDefinitionForMutationAsync(roleDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException($"Role definition {roleDefinitionId} not found");

        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            throw new InvalidOperationException("User does not have permission to manage role definitions for this team");

        if (slotCount < 1)
            throw new InvalidOperationException("Slot count must be at least 1");

        if (priorities.Count != slotCount)
            throw new InvalidOperationException($"Priorities count ({priorities.Count}) must match slot count ({slotCount})");

        if (slotCount < definition.Assignments.Count)
            throw new InvalidOperationException(
                $"Cannot reduce slot count to {slotCount} — {definition.Assignments.Count} slots are currently filled");

        if (!string.Equals(definition.Name, name, StringComparison.Ordinal))
        {
            ValidateRoleName(name);
            var lowerName = name.ToLowerInvariant();
            var nameExists = await _repo.RoleDefinitionNameExistsAsync(definition.TeamId, lowerName, excludingId: roleDefinitionId, cancellationToken);
            if (nameExists)
                throw new InvalidOperationException($"A role definition with name '{name}' already exists for this team");
        }

        definition.Name = name;
        definition.Description = description;
        definition.SlotCount = slotCount;
        definition.Priorities = priorities;
        definition.SortOrder = sortOrder;

        var usersNeedingShiftAuthorizationInvalidation =
            definition.Team.SystemTeamType == SystemTeamType.None &&
            definition.IsManagement != isManagement &&
            definition.Assignments.Count > 0
                ? definition.Assignments
                    .Select(a => a.TeamMember?.UserId)
                    .Where(uid => uid.HasValue)
                    .Select(uid => uid!.Value)
                    .Distinct()
                    .ToList()
                : [];

        if (isManagement && !definition.IsManagement)
        {
            var existingManagement = await _repo.OtherRoleHasIsManagementAsync(
                definition.TeamId, roleDefinitionId, cancellationToken);
            if (existingManagement)
                throw new InvalidOperationException("Another role in this team is already marked as the management role");
        }

        var clearingIsManagement = definition.IsManagement && !isManagement;

        definition.IsPublic = isPublic;
        definition.IsManagement = isManagement;
        definition.Period = period;
        definition.UpdatedAt = _clock.GetCurrentInstant();

        var (_, invalidatedActiveTeams) = await _repo.PersistRoleDefinitionUpdateAsync(
            definition, clearingIsManagement, cancellationToken);

        await _auditLogService.LogAsync(
            AuditAction.TeamRoleDefinitionUpdated, nameof(TeamRoleDefinition), definition.Id,
            $"Role definition '{name}' updated for team {definition.Team.Name}",
            actorUserId,
            relatedEntityId: definition.TeamId, relatedEntityType: nameof(Team));

        if (invalidatedActiveTeams)
            InvalidateActiveTeamsCache();

        InvalidateShiftAuthorization(usersNeedingShiftAuthorizationInvalidation);

        return definition;
    }

    public async Task DeleteRoleDefinitionAsync(
        Guid roleDefinitionId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var definition = await _repo.FindRoleDefinitionForMutationAsync(roleDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException($"Role definition {roleDefinitionId} not found");

        if (definition.IsManagement && definition.Assignments.Count > 0)
            throw new InvalidOperationException("Cannot delete the management role while members are assigned to it. Unassign all members first.");

        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            throw new InvalidOperationException("User does not have permission to manage role definitions for this team");

        await _repo.RemoveRoleDefinitionAsync(definition, cancellationToken);

        await _auditLogService.LogAsync(
            AuditAction.TeamRoleDefinitionDeleted, nameof(TeamRoleDefinition), definition.Id,
            $"Role definition '{definition.Name}' deleted from team {definition.Team.Name}",
            actorUserId,
            relatedEntityId: definition.TeamId, relatedEntityType: nameof(Team));
    }

    public async Task SetRoleIsManagementAsync(
        Guid roleDefinitionId, bool isManagement, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var definition = await _repo.FindRoleDefinitionWithMembersForMutationAsync(roleDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException($"Role definition {roleDefinitionId} not found");

        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            throw new InvalidOperationException("User does not have permission to manage role definitions for this team");

        var usersNeedingShiftAuthorizationInvalidation =
            IsShiftAuthorizationDefinition(definition) &&
            definition.IsManagement != isManagement &&
            definition.Assignments.Count > 0
                ? definition.Assignments
                    .Select(a => a.TeamMember?.UserId)
                    .Where(uid => uid.HasValue)
                    .Select(uid => uid!.Value)
                    .Distinct()
                    .ToList()
                : [];

        if (isManagement)
        {
            if (definition.Assignments.Count > 0)
                throw new InvalidOperationException("Cannot set IsManagement while members are assigned to the role");

            var existingManagement = await _repo.OtherRoleHasIsManagementAsync(
                definition.TeamId, roleDefinitionId, cancellationToken);
            if (existingManagement)
                throw new InvalidOperationException("Another role in this team is already marked as the management role");
        }

        var clearingIsManagement = definition.IsManagement && !isManagement && definition.Assignments.Count > 0;

        definition.IsManagement = isManagement;
        definition.UpdatedAt = _clock.GetCurrentInstant();

        var demotedMembers = await _repo.PersistRoleIsManagementAsync(
            definition, clearingIsManagement, cancellationToken);

        await _auditLogService.LogAsync(
            AuditAction.TeamRoleDefinitionUpdated, nameof(TeamRoleDefinition), definition.Id,
            $"IsManagement set to {isManagement} on role '{definition.Name}' in {definition.Team.Name}",
            actorUserId,
            relatedEntityId: definition.TeamId, relatedEntityType: nameof(Team));

        if (demotedMembers)
            InvalidateActiveTeamsCache();

        InvalidateShiftAuthorization(usersNeedingShiftAuthorizationInvalidation);
    }

    public async Task<IReadOnlyList<TeamRoleDefinition>> GetRoleDefinitionsAsync(
        Guid teamId, CancellationToken cancellationToken = default)
    {
        var definitions = await _repo.GetRoleDefinitionsAsync(teamId, cancellationToken);
        await StitchRoleAssignmentUserSlicesAsync(definitions, cancellationToken);
        return definitions;
    }

    public async Task<IReadOnlyList<TeamRoleDefinition>> GetAllRoleDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        var definitions = await _repo.GetAllRoleDefinitionsAsync(cancellationToken);
        await StitchRoleAssignmentUserSlicesAsync(definitions, cancellationToken);
        return definitions;
    }

    public async Task<IReadOnlyList<TeamRosterSlotSummary>> GetRosterAsync(
        string? priority,
        string? status,
        string? period,
        CancellationToken cancellationToken = default)
    {
        var definitions = await GetAllRoleDefinitionsAsync(cancellationToken);

        var slots = new List<TeamRosterSlotSummary>();
        foreach (var definition in definitions)
        {
            for (var slotIndex = 0; slotIndex < definition.SlotCount; slotIndex++)
            {
                var assignment = definition.Assignments.FirstOrDefault(a => a.SlotIndex == slotIndex);
                var slotPriority = slotIndex < definition.Priorities.Count
                    ? definition.Priorities[slotIndex]
                    : SlotPriority.None;

                slots.Add(new TeamRosterSlotSummary(
                    definition.Team.Name,
                    definition.Team.Slug,
                    definition.Name,
                    definition.Description,
                    definition.Id,
                    slotIndex + 1,
                    slotPriority.ToString(),
                    GetPriorityBadgeClass(slotPriority),
                    definition.Period.ToString(),
                    assignment is not null,
                    assignment?.TeamMember?.UserId,
#pragma warning disable CS0618 // User slice stitched in-memory by StitchRoleAssignmentUserSlicesAsync
                    assignment?.TeamMember?.User?.DisplayName));
#pragma warning restore CS0618
            }
        }

        if (!string.IsNullOrEmpty(priority))
            slots = slots.Where(s => string.Equals(s.Priority, priority, StringComparison.OrdinalIgnoreCase)).ToList();

        if (string.Equals(status, "Open", StringComparison.OrdinalIgnoreCase))
            slots = slots.Where(s => !s.IsFilled).ToList();
        else if (string.Equals(status, "Filled", StringComparison.OrdinalIgnoreCase))
            slots = slots.Where(s => s.IsFilled).ToList();

        if (!string.IsNullOrEmpty(period))
            slots = slots.Where(s => string.Equals(s.Period, period, StringComparison.OrdinalIgnoreCase)).ToList();

        return slots
            .OrderBy(slot => slot.Priority switch
            {
                nameof(SlotPriority.Critical) => 0,
                nameof(SlotPriority.Important) => 1,
                nameof(SlotPriority.NiceToHave) => 2,
                _ => 3
            })
            .ThenBy(slot => slot.TeamName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(slot => slot.RoleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(slot => slot.SlotNumber)
            .ToList();
    }

    // ==========================================================================
    // Team Role Assignments
    // ==========================================================================

    public async Task<TeamRoleAssignment> AssignToRoleAsync(
        Guid roleDefinitionId, Guid targetUserId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var definition = await _repo.FindRoleDefinitionForMutationAsync(roleDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException($"Role definition {roleDefinitionId} not found");

        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            throw new InvalidOperationException("User does not have permission to manage role assignments for this team");

        var existingMember = await _repo.FindActiveMemberForMutationAsync(definition.TeamId, targetUserId, cancellationToken);
        var now = _clock.GetCurrentInstant();

        TeamMember? autoAddMember = null;
        GoogleSyncOutboxEvent? outboxEvent = null;
        if (existingMember is null)
        {
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

        var (assignment, autoAddedToTeam, persistedMember) = await _repo.AssignToRoleAsync(
            roleDefinitionId,
            targetUserId,
            actorUserId,
            autoAddMember,
            outboxEvent,
            promoteToCoordinator: definition.IsManagement,
            now,
            cancellationToken);

        if (autoAddedToTeam)
        {
            await _auditLogService.LogAsync(
                AuditAction.TeamMemberAdded, nameof(Team), definition.TeamId,
                $"Auto-added to {definition.Team.Name} via role assignment",
                actorUserId,
                relatedEntityId: targetUserId, relatedEntityType: nameof(User));
        }

        await _auditLogService.LogAsync(
            AuditAction.TeamRoleAssigned, nameof(TeamRoleDefinition), roleDefinitionId,
            $"Assigned to role '{definition.Name}' in {definition.Team.Name}",
            actorUserId,
            relatedEntityId: targetUserId, relatedEntityType: nameof(User));

        InvalidateShiftAuthorizationIfNeeded(definition, targetUserId);

        var targetUser = await UserService.GetByIdAsync(targetUserId, cancellationToken);
        if (targetUser is not null)
        {
            var cachedMember = new CachedTeamMember(
                persistedMember.Id, targetUserId, targetUser.DisplayName, targetUser.ProfilePictureUrl,
                persistedMember.Role, persistedMember.JoinedAt);
            TryUpdateCachedTeam(definition.TeamId, ct =>
            {
                var existing = ct.Members.FirstOrDefault(m => m.UserId == targetUserId);
                return existing is not null
                    ? ct with { Members = ct.Members.Select(m => m.UserId == targetUserId ? cachedMember : m).ToList() }
                    : ct with { Members = [.. ct.Members, cachedMember] };
            });
        }

        return assignment;
    }

    public async Task UnassignFromRoleAsync(
        Guid roleDefinitionId, Guid teamMemberId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var definition = await _repo.FindRoleDefinitionForMutationAsync(roleDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException($"Role definition {roleDefinitionId} not found");

        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            throw new InvalidOperationException("User does not have permission to manage role assignments for this team");

        var (demoted, targetUserId) = await _repo.UnassignFromRoleAsync(
            roleDefinitionId, teamMemberId, cancellationToken);

        await _auditLogService.LogAsync(
            AuditAction.TeamRoleUnassigned, nameof(TeamRoleDefinition), roleDefinitionId,
            $"Unassigned from role '{definition.Name}' in {definition.Team.Name}",
            actorUserId,
            relatedEntityId: targetUserId, relatedEntityType: nameof(User));

        InvalidateShiftAuthorizationIfNeeded(definition, targetUserId);

        if (definition.IsManagement && demoted)
            UpdateMemberRoleInTeamCache(definition.TeamId, targetUserId, TeamMemberRole.Member);
    }

    // ==========================================================================
    // Coordinator / Member queries
    // ==========================================================================

    public Task<IReadOnlyList<Guid>> GetUserCoordinatedTeamIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        _repo.GetUserCoordinatorTeamIdsAsync(userId, cancellationToken);

    public Task<IReadOnlyList<Guid>> GetCoordinatorUserIdsAsync(
        Guid teamId,
        CancellationToken cancellationToken = default) =>
        _repo.GetCoordinatorUserIdsAsync(teamId, cancellationToken);

    public async Task<IReadOnlyList<string>> GetActiveTeamNamesForUserAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var cached = await GetCachedTeamsAsync(cancellationToken);
        return cached.Values
            .Where(t => t.SystemTeamType != SystemTeamType.Volunteers
                && t.Members.Any(m => m.UserId == userId))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task EnqueueGoogleResyncForUserTeamsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var count = await _repo.EnqueueResyncEventsForUserAsync(userId, now, cancellationToken);
        if (count > 0)
        {
            _logger.LogInformation(
                "Enqueued {Count} re-sync events for user {UserId} after Google email change",
                count, userId);
        }
    }

    public Task<IReadOnlyDictionary<Guid, Team>> GetByIdsWithParentsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken = default) =>
        _repo.GetByIdsWithParentsAsync(teamIds, cancellationToken);

    public Task<IReadOnlyList<TeamCoordinatorRef>> GetActiveCoordinatorsForTeamsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken = default) =>
        _repo.GetActiveCoordinatorsForTeamsAsync(teamIds, cancellationToken);

    public async Task<TeamMember> AddSeededMemberAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Instant joinedAt,
        CancellationToken cancellationToken = default)
    {
        var team = await _repo.GetByIdAsync(teamId, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
            throw new InvalidOperationException("AddSeededMemberAsync cannot target system teams");

        var existing = await _repo.IsActiveMemberAsync(teamId, userId, cancellationToken);
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

        await _repo.AddMemberAsync(member, cancellationToken);

        var addedUser = await UserService.GetByIdAsync(userId, cancellationToken);
        if (addedUser is not null)
        {
            AddMemberToTeamCache(teamId, new CachedTeamMember(
                member.Id, userId, addedUser.DisplayName, addedUser.ProfilePictureUrl, role, joinedAt));
        }

        return member;
    }

    public Task<int> HardDeleteSeededTeamsAsync(
        string nameSuffix,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(nameSuffix))
            throw new ArgumentException("nameSuffix must be a non-empty marker", nameof(nameSuffix));

        return HardDeleteAndRemoveFromCacheAsync(nameSuffix, cancellationToken);
    }

    private async Task<int> HardDeleteAndRemoveFromCacheAsync(string nameSuffix, CancellationToken ct)
    {
        var count = await _repo.HardDeleteBySuffixAsync(nameSuffix, ct);
        if (count > 0)
        {
            // Wipe the whole cache — we don't track which ids were removed.
            _cache.InvalidateActiveTeams();
        }
        return count;
    }

    public Task<IReadOnlyDictionary<Guid, int>> GetPendingRequestCountsByTeamIdsAsync(
        IEnumerable<Guid> teamIds,
        CancellationToken cancellationToken = default) =>
        _repo.GetPendingCountsByTeamIdsAsync(teamIds.ToList(), cancellationToken);

    public Task<int> GetTotalPendingJoinRequestCountAsync(CancellationToken cancellationToken = default) =>
        _repo.GetTotalPendingCountAsync(cancellationToken);

    public Task<IReadOnlyList<Guid>> GetActiveNonSystemTeamCoordinatorUserIdsAsync(
        CancellationToken cancellationToken = default) =>
        _repo.GetActiveNonSystemTeamCoordinatorUserIdsAsync(cancellationToken);

    public async Task<IReadOnlyList<TeamMember>> GetActiveMembersForTeamsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken = default)
    {
        if (teamIds.Count == 0)
            return [];
        var members = await _repo.GetActiveMembersForTeamsAsync(teamIds, cancellationToken);
        await StitchMemberUserSlicesAsync(members, cancellationToken);
        return members;
    }

    public async Task<IReadOnlyList<TeamMember>> GetActiveChildMembersByParentIdsAsync(
        IReadOnlyCollection<Guid> parentTeamIds,
        CancellationToken cancellationToken = default)
    {
        if (parentTeamIds.Count == 0)
            return [];
        var members = await _repo.GetActiveChildMembersByParentIdsAsync(parentTeamIds, cancellationToken);
        await StitchMemberUserSlicesAsync(members, cancellationToken);
        return members;
    }

    public Task<IReadOnlyDictionary<Guid, string>> GetManagementRoleNamesByTeamIdsAsync(
        IEnumerable<Guid> teamIds,
        CancellationToken cancellationToken = default) =>
        _repo.GetPublicManagementRoleNamesByTeamIdsAsync(teamIds.ToList(), cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, List<string>>> GetNonSystemTeamNamesByUserIdsAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        var userIdSet = userIds.ToHashSet();
        if (userIdSet.Count == 0)
            return new Dictionary<Guid, List<string>>();

        var cached = await GetCachedTeamsAsync(cancellationToken);
        var result = new Dictionary<Guid, List<string>>();

        foreach (var team in cached.Values.Where(t => t.SystemTeamType == SystemTeamType.None && !t.IsHidden))
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

        return result;
    }

    public Task<(IReadOnlyList<Team> Items, int TotalCount)> GetAllTeamsForAdminAsync(
        int page, int pageSize, CancellationToken cancellationToken = default) =>
        _repo.GetAllForAdminAsync(page, pageSize, cancellationToken);

    public async Task<AdminTeamListResult> GetAdminTeamListAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var (items, totalCount) = await _repo.GetAllForAdminAsync(page, pageSize, cancellationToken);

        var activeEvent = await _shiftManagementService.GetActiveAsync();
        var activeEventId = activeEvent?.Id ?? Guid.Empty;

        var pendingShiftCounts = activeEventId == Guid.Empty
            ? new Dictionary<Guid, int>()
            : await _shiftManagementService.GetPendingShiftSignupCountsByTeamAsync(
                activeEventId, cancellationToken);

        var teamIds = items.Select(t => t.Id).ToList();
        var resourceSummaries = await TeamResourceService
            .GetTeamResourceSummariesAsync(teamIds, cancellationToken);

        return new AdminTeamListResult(
            BuildAdminTeamSummaries(items, pendingShiftCounts, resourceSummaries),
            totalCount);
    }

    // ==========================================================================
    // Cache helpers — public surface
    // ==========================================================================

    public void RemoveMemberFromAllTeamsCache(Guid userId)
    {
        TryMutateActiveTeamsCache(cached =>
        {
            foreach (var kvp in cached)
            {
                if (kvp.Value.Members.Any(m => m.UserId == userId))
                    cached[kvp.Key] = kvp.Value with { Members = kvp.Value.Members.Where(m => m.UserId != userId).ToList() };
            }
        });
    }

    public void InvalidateActiveTeamsCache() => _cache.InvalidateActiveTeams();

    public async Task<int> RevokeAllMembershipsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var count = await _repo.RevokeAllMembershipsAsync(userId, now, cancellationToken);
        if (count > 0)
            RemoveMemberFromAllTeamsCache(userId);
        return count;
    }

    // ==========================================================================
    // GDPR export
    // ==========================================================================

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var memberships = await _repo.GetAllMembershipsForUserAsync(userId, ct);
        var joinRequests = await _repo.GetAllJoinRequestsForUserAsync(userId, ct);

        var membershipSlice = new UserDataSlice(GdprExportSections.TeamMemberships, memberships.Select(tm => new
        {
            TeamName = tm.Team.Name,
            tm.Role,
            JoinedAt = tm.JoinedAt.ToInvariantInstantString(),
            LeftAt = tm.LeftAt.ToInvariantInstantString(),
            TeamRoles = tm.RoleAssignments.Select(tra => new
            {
                RoleName = tra.TeamRoleDefinition.Name,
                AssignedAt = tra.AssignedAt.ToInvariantInstantString()
            })
        }).ToList());

        var joinRequestSlice = new UserDataSlice(GdprExportSections.TeamJoinRequests, joinRequests.Select(tjr => new
        {
            TeamName = tjr.Team.Name,
            tjr.Status,
            tjr.Message,
            RequestedAt = tjr.RequestedAt.ToInvariantInstantString(),
            ResolvedAt = tjr.ResolvedAt.ToInvariantInstantString()
        }).ToList());

        return [membershipSlice, joinRequestSlice];
    }

    // ==========================================================================
    // Internal helpers — cache
    // ==========================================================================

    private async Task<ConcurrentDictionary<Guid, CachedTeam>> GetCachedTeamsAsync(CancellationToken ct = default)
    {
        return await _cache.GetOrCreateAsync(CacheKeys.ActiveTeams, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);

            var teams = await _repo.GetAllActiveWithMembersAsync(ct);
            // Stitch the member User slice in memory (cross-domain).
            var allUserIds = teams.SelectMany(t => t.Members.Where(m => m.LeftAt is null).Select(m => m.UserId))
                .Distinct()
                .ToList();
            var users = allUserIds.Count == 0
                ? new Dictionary<Guid, User>()
                : await UserService.GetByIdsAsync(allUserIds, ct);

            return new ConcurrentDictionary<Guid, CachedTeam>(
                teams.ToDictionary(t => t.Id, t => BuildCachedTeam(t, users)));
        }) ?? new();
    }

    private static CachedTeam BuildCachedTeam(Team team, IReadOnlyDictionary<Guid, User> users) => new(
        Id: team.Id,
        Name: team.Name,
        Description: team.Description,
        Slug: team.Slug,
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
                return new CachedTeamMember(
                    TeamMemberId: m.Id,
                    UserId: m.UserId,
                    DisplayName: u?.DisplayName ?? string.Empty,
                    ProfilePictureUrl: u?.ProfilePictureUrl,
                    Role: m.Role,
                    JoinedAt: m.JoinedAt);
            })
            .ToList(),
        ParentTeamId: team.ParentTeamId);

    private bool TryMutateActiveTeamsCache(Action<ConcurrentDictionary<Guid, CachedTeam>> mutate) =>
        _cache.TryUpdateExistingValue<ConcurrentDictionary<Guid, CachedTeam>>(CacheKeys.ActiveTeams, mutate);

    private void UpsertCachedTeam(CachedTeam team) =>
        TryMutateActiveTeamsCache(cachedTeams => cachedTeams[team.Id] = team);

    private void RemoveCachedTeam(Guid teamId) =>
        TryMutateActiveTeamsCache(cachedTeams => cachedTeams.TryRemove(teamId, out _));

    private bool TryUpdateCachedTeam(Guid teamId, Func<CachedTeam, CachedTeam> update)
    {
        var updated = false;

        TryMutateActiveTeamsCache(cachedTeams =>
        {
            if (!cachedTeams.TryGetValue(teamId, out var team))
                return;

            cachedTeams[teamId] = update(team);
            updated = true;
        });

        return updated;
    }

    private void AddMemberToTeamCache(Guid teamId, CachedTeamMember member) =>
        TryUpdateCachedTeam(teamId, team => team with { Members = [.. team.Members, member] });

    private void RemoveMemberFromTeamCache(Guid teamId, Guid userId) =>
        TryUpdateCachedTeam(teamId, team => team with { Members = team.Members.Where(m => m.UserId != userId).ToList() });

    private void UpdateMemberRoleInTeamCache(Guid teamId, Guid userId, TeamMemberRole role) =>
        TryUpdateCachedTeam(teamId, team => team with
        {
            Members = team.Members
                .Select(m => m.UserId == userId ? m with { Role = role } : m)
                .ToList()
        });

    // ==========================================================================
    // Internal helpers — shift authorization invalidation
    // ==========================================================================

    private void InvalidateShiftAuthorizationIfNeeded(Guid userId, IEnumerable<TeamRoleAssignment> roleAssignments)
    {
        if (roleAssignments.Any(IsShiftAuthorizationAssignment))
            _shiftAuthInvalidator.Invalidate(userId);
    }

    private void InvalidateShiftAuthorizationIfNeeded(TeamRoleDefinition definition, Guid userId)
    {
        if (IsShiftAuthorizationDefinition(definition))
            _shiftAuthInvalidator.Invalidate(userId);
    }

    private void InvalidateShiftAuthorization(IEnumerable<Guid> userIds)
    {
        foreach (var userId in userIds.Distinct())
            _shiftAuthInvalidator.Invalidate(userId);
    }

    private static bool IsShiftAuthorizationAssignment(TeamRoleAssignment assignment) =>
        IsShiftAuthorizationDefinition(assignment.TeamRoleDefinition);

    private static bool IsShiftAuthorizationDefinition(TeamRoleDefinition definition) =>
        definition.IsManagement &&
        definition.Team.SystemTeamType == SystemTeamType.None;

    // ==========================================================================
    // Internal helpers — user stitching
    // ==========================================================================

    private async Task StitchMemberUserSlicesAsync(
        IEnumerable<TeamMember> members, CancellationToken ct)
    {
        var list = members as IReadOnlyList<TeamMember> ?? members.ToList();
        if (list.Count == 0)
            return;

        var userIds = list.Select(m => m.UserId).Distinct().ToList();
        var users = await UserService.GetByIdsAsync(userIds, ct);

        foreach (var member in list)
        {
            if (users.TryGetValue(member.UserId, out var user))
            {
                // Populate the cross-domain nav property in-memory (§6b in-memory join).
                // Callers that still use `.User.DisplayName` / `.User.Email` continue
                // to work; the nav is ObsoleteAttribute-marked to gate new reads.
#pragma warning disable CS0618
                member.User = user;
#pragma warning restore CS0618
            }
        }
    }

    private async Task StitchJoinRequestUserSlicesAsync(
        IReadOnlyList<TeamJoinRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0)
            return;

        var userIds = requests.Select(r => r.UserId)
            .Concat(requests.Where(r => r.ReviewedByUserId.HasValue).Select(r => r.ReviewedByUserId!.Value))
            .Distinct()
            .ToList();
        var users = await UserService.GetByIdsAsync(userIds, ct);

        foreach (var req in requests)
        {
            if (users.TryGetValue(req.UserId, out var u))
            {
#pragma warning disable CS0618
                req.User = u;
#pragma warning restore CS0618
            }
            if (req.ReviewedByUserId.HasValue && users.TryGetValue(req.ReviewedByUserId.Value, out var r))
            {
#pragma warning disable CS0618
                req.ReviewedByUser = r;
#pragma warning restore CS0618
            }
        }
    }

    private async Task StitchRoleAssignmentUserSlicesAsync(
        IReadOnlyList<TeamRoleDefinition> definitions, CancellationToken ct)
    {
        var members = definitions
            .SelectMany(d => d.Assignments.Select(a => a.TeamMember))
            .Where(m => m is not null)
            .ToList();
        if (members.Count == 0)
            return;

        var userIds = members.Select(m => m.UserId).Distinct().ToList();
        var users = await UserService.GetByIdsAsync(userIds, ct);

        foreach (var member in members)
        {
            if (users.TryGetValue(member.UserId, out var user))
            {
#pragma warning disable CS0618
                member.User = user;
#pragma warning restore CS0618
            }
        }
    }

    // ==========================================================================
    // Internal helpers — outbox + email
    // ==========================================================================

    private async Task<GoogleSyncOutboxEvent?> BuildOutboxEventAsync(
        Guid teamMemberId,
        Guid teamId,
        Guid userId,
        string eventType,
        CancellationToken ct)
    {
        var user = await UserService.GetByIdAsync(userId, ct);
        if (user?.GoogleEmailStatus == GoogleEmailStatus.Rejected)
        {
            _logger.LogDebug(
                "Skipping Google sync outbox event {EventType} for user {UserId} — GoogleEmailStatus is Rejected",
                eventType, userId);
            return null;
        }

        return new GoogleSyncOutboxEvent
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            TeamId = teamId,
            UserId = userId,
            OccurredAt = _clock.GetCurrentInstant(),
            DeduplicationKey = $"{teamMemberId}:{eventType}"
        };
    }

    private async Task SendAddedToTeamEmailAsync(Guid userId, Team team, CancellationToken cancellationToken)
    {
        if (team.IsHidden) return;

        try
        {
            var user = await UserService.GetByIdAsync(userId, cancellationToken);
            if (user is null) return;

#pragma warning disable CS0618 // User.GetEffectiveEmail() cross-domain — tracked in §15i nav-strip follow-up
            var email = user.GetEffectiveEmail() ?? user.Email!;
#pragma warning restore CS0618
            var resources = await TeamResourceService.GetTeamResourcesAsync(team.Id, cancellationToken);

            await EmailService.SendAddedToTeamAsync(
                email, user.DisplayName, team.Name, team.Slug,
                resources.Select(r => (r.Name, r.Url)),
                user.PreferredLanguage,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send added-to-team email for user {UserId} team {TeamId}", userId, team.Id);
        }

        try
        {
            await _notificationService.SendAsync(
                NotificationSource.TeamMemberAdded,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                $"You were added to {team.Name}",
                [userId],
                actionUrl: $"/Teams/{team.Slug}",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch added-to-team inbox notification for user {UserId} team {TeamId}", userId, team.Id);
        }
    }

    // ==========================================================================
    // Static projection helpers
    // ==========================================================================

    private static IReadOnlyList<AdminTeamSummary> BuildAdminTeamSummaries(
        IReadOnlyList<Team> teams,
        IReadOnlyDictionary<Guid, int> pendingShiftCounts,
        IReadOnlyDictionary<Guid, TeamResourceSummary> resourceSummaries)
    {
        var ordered = new List<AdminTeamSummary>(teams.Count);

        foreach (var team in teams)
        {
            if (team.ParentTeamId.HasValue)
                continue;

            ordered.Add(CreateAdminTeamSummary(team, isChildTeam: false, pendingShiftCounts, resourceSummaries));

            var children = teams
                .Where(child => child.ParentTeamId == team.Id)
                .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase);

            ordered.AddRange(children.Select(child =>
                CreateAdminTeamSummary(child, isChildTeam: true, pendingShiftCounts, resourceSummaries)));
        }

        return ordered;
    }

    private static AdminTeamSummary CreateAdminTeamSummary(
        Team team,
        bool isChildTeam,
        IReadOnlyDictionary<Guid, int> pendingShiftCounts,
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
            isChildTeam,
            pendingShiftCounts.GetValueOrDefault(team.Id),
            team.IsHidden);
    }

    private static TeamDirectorySummary CreateDirectorySummary(
        CachedTeam team,
        IReadOnlyDictionary<Guid, CachedTeam> teamById,
        Guid? userId)
    {
        CachedTeam? parent = team.ParentTeamId.HasValue && teamById.TryGetValue(team.ParentTeamId.Value, out var resolvedParent)
            ? resolvedParent
            : null;
        var isCurrentUserMember = userId.HasValue && team.Members.Any(m => m.UserId == userId.Value);
        var isCurrentUserCoordinator = userId.HasValue && team.Members.Any(m =>
            m.UserId == userId.Value &&
            m.Role == TeamMemberRole.Coordinator);

        return new TeamDirectorySummary(
            team.Id,
            team.Name,
            team.Description,
            team.Slug,
            team.Members.Count,
            team.IsSystemTeam,
            team.RequiresApproval,
            team.IsPublicPage,
            isCurrentUserMember,
            isCurrentUserCoordinator,
            parent?.Name,
            parent?.Slug);
    }

    private static TeamDetailMemberSummary MapTeamDetailMemberSummary(TeamMember member) => new(
        UserId: member.UserId,
#pragma warning disable CS0618 // populated in memory
        DisplayName: member.User?.DisplayName ?? string.Empty,
        Email: member.User?.Email,
        ProfilePictureUrl: member.User?.ProfilePictureUrl,
#pragma warning restore CS0618
        Role: member.Role,
        JoinedAt: member.JoinedAt);

    private static string GetPriorityBadgeClass(SlotPriority priority) =>
        priority switch
        {
            SlotPriority.Critical => "bg-danger",
            SlotPriority.Important => "bg-warning text-dark",
            SlotPriority.NiceToHave => "bg-secondary",
            _ => "bg-light text-dark"
        };

    private static void ValidateRoleName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Role name cannot be empty");

        if (name.Length > 100)
            throw new InvalidOperationException("Role name cannot exceed 100 characters");
    }
}
