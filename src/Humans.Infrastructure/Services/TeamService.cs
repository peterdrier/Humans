using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using Humans.Infrastructure.Data;
using System.Text.RegularExpressions;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Service for managing teams and team membership.
/// </summary>
public class TeamService : ITeamService
{
    private readonly HumansDbContext _dbContext;
    private readonly IAuditLogService _auditLogService;
    private readonly IEmailService _emailService;
    private readonly INotificationService _notificationService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IShiftManagementService _shiftManagementService;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TeamService> _logger;

    public TeamService(
        HumansDbContext dbContext,
        IAuditLogService auditLogService,
        IEmailService emailService,
        INotificationService notificationService,
        IRoleAssignmentService roleAssignmentService,
        IShiftManagementService shiftManagementService,
        IClock clock,
        IMemoryCache cache,
        ILogger<TeamService> logger)
    {
        _dbContext = dbContext;
        _auditLogService = auditLogService;
        _emailService = emailService;
        _notificationService = notificationService;
        _roleAssignmentService = roleAssignmentService;
        _shiftManagementService = shiftManagementService;
        _clock = clock;
        _cache = cache;
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
        var baseSlug = Helpers.SlugHelper.GenerateSlug(name);
        var now = _clock.GetCurrentInstant();

        // Block reserved slugs (static routes in TeamController)
        string[] reservedSlugs = ["roster", "birthdays", "map", "my", "sync", "summary", "create", "search"];
        if (Array.Exists(reservedSlugs, s => string.Equals(baseSlug, s, StringComparison.Ordinal)))
            throw new InvalidOperationException($"The team name '{name}' conflicts with a reserved route");

        if (parentTeamId.HasValue)
        {
            var parent = await _dbContext.Teams
                .Include(t => t.ChildTeams)
                .FirstOrDefaultAsync(t => t.Id == parentTeamId.Value, cancellationToken)
                ?? throw new InvalidOperationException($"Parent team {parentTeamId.Value} not found");

            if (parent.IsSystemTeam)
                throw new InvalidOperationException("System teams cannot be parents");

            if (parent.ParentTeamId.HasValue)
                throw new InvalidOperationException("Cannot nest more than one level — the parent team already has a parent");
        }

        // Retry with incrementing suffix on unique constraint violation or custom slug collision
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var slug = attempt == 0 ? baseSlug : $"{baseSlug}-{attempt + 1}";

            // Check if slug collides with another team's slug or custom slug
            var collidesWithExistingSlug = await _dbContext.Teams.AnyAsync(
                t => t.Slug == slug || t.CustomSlug == slug, cancellationToken);
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

            _dbContext.Teams.Add(team);

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);

                if (!requiresApproval)
                {
                    // RequiresApproval has a store default of true, so persist explicit false
                    // after insert instead of relying on EF's insert sentinel handling.
                    var requiresApprovalEntry = _dbContext.Entry(team).Property(t => t.RequiresApproval);
                    requiresApprovalEntry.CurrentValue = false;
                    requiresApprovalEntry.IsModified = true;
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }

                UpsertCachedTeam(new CachedTeam(team.Id, team.Name, team.Description, team.Slug,
                    team.IsSystemTeam, team.SystemTeamType, team.RequiresApproval, team.IsPublicPage, team.IsHidden,
                    team.CreatedAt, [], ParentTeamId: parentTeamId));
                _logger.LogInformation("Created team {TeamName} with slug {Slug}", name, slug);
                return team;
            }
            catch (DbUpdateException ex) when (attempt < 9)
            {
                // Slug collision — detach and retry with next suffix
                _logger.LogDebug(ex, "Slug collision for '{Slug}', retrying (attempt {Attempt})", slug, attempt + 1);
                _dbContext.Entry(team).State = EntityState.Detached;
            }
        }

        throw new InvalidOperationException($"Could not generate unique slug for team '{name}' after 10 attempts");
    }

    public async Task<Team?> GetTeamBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        var normalizedSlug = slug.ToLowerInvariant();

        return await _dbContext.Teams
            .AsNoTracking()
            .Include(t => t.Members.Where(m => m.LeftAt == null))
                .ThenInclude(m => m.User)
            .Include(t => t.ParentTeam)
            .Include(t => t.ChildTeams)
            .FirstOrDefaultAsync(t => t.Slug == normalizedSlug || t.CustomSlug == normalizedSlug, cancellationToken);
    }

    public async Task<Team?> GetTeamByIdAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Teams
            .AsNoTracking()
            .Include(t => t.Members.Where(m => m.LeftAt == null))
                .ThenInclude(m => m.User)
            .FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
    }

    public async Task<IReadOnlyList<Team>> GetAllTeamsAsync(CancellationToken cancellationToken = default)
    {
        // Still used by callers that need Team entities (admin pages).
        // High-frequency reads should use GetCachedTeamsAsync() via specific methods.
        return await _dbContext.Teams
            .AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name)
            .Include(t => t.Members.Where(m => m.LeftAt == null))
            .Include(t => t.ChildTeams)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Team>> GetUserCreatedTeamsAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Teams
            .AsNoTracking()
            .Where(t => t.IsActive && t.SystemTeamType == SystemTeamType.None)
            .OrderBy(t => t.Name)
            .Include(t => t.Members.Where(m => m.LeftAt == null))
            .ToListAsync(cancellationToken);
    }

    public async Task<TeamDirectoryResult> GetTeamDirectoryAsync(
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        var cachedTeams = await GetCachedTeamsAsync(cancellationToken);

        if (!userId.HasValue)
        {
            var publicDepartments = cachedTeams.Values
                .Where(t => t.IsPublicPage && !t.IsSystemTeam && !t.IsHidden && t.ParentTeamId is null)
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

        var isBoardMember = await _roleAssignmentService.IsUserBoardMemberAsync(userId.Value, cancellationToken);
        var isAdmin = await _roleAssignmentService.IsUserAdminAsync(userId.Value, cancellationToken);
        var isTeamsAdmin = await _roleAssignmentService.IsUserTeamsAdminAsync(userId.Value, cancellationToken);
        var canCreateTeam = isBoardMember || isAdmin || isTeamsAdmin;
        var canSeeHiddenTeams = canCreateTeam; // Admin, Board, and TeamsAdmin can see hidden teams

        var visibleTeams = canSeeHiddenTeams
            ? cachedTeams.Values
            : cachedTeams.Values.Where(t => !t.IsHidden);

        var summaries = visibleTeams
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
        {
            return null;
        }

        if (!userId.HasValue && (!team.IsPublicPage || team.IsHidden))
        {
            return null;
        }

        var activeMembers = team.Members
            .Where(m => m.LeftAt is null)
            .ToList();

        if (!userId.HasValue)
        {
            var coordinators = activeMembers
                .Where(m => m.Role == TeamMemberRole.Coordinator)
                .OrderBy(m => m.User.DisplayName, StringComparer.OrdinalIgnoreCase)
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
        var isBoardMember = await _roleAssignmentService.IsUserBoardMemberAsync(currentUserId, cancellationToken);
        var isAdmin = await _roleAssignmentService.IsUserAdminAsync(currentUserId, cancellationToken);
        var isTeamsAdmin = await _roleAssignmentService.IsUserTeamsAdminAsync(currentUserId, cancellationToken);
        var canManage = isCurrentUserCoordinator || isBoardMember || isAdmin || isTeamsAdmin;

        // Hidden teams are only visible to Admin, Board, and TeamsAdmin
        if (team.IsHidden && !isBoardMember && !isAdmin && !isTeamsAdmin)
        {
            return null;
        }
        var pendingRequest = await GetUserPendingRequestAsync(team.Id, currentUserId, cancellationToken);
        var pendingRequestCount = canManage
            ? (await GetPendingRequestsForTeamAsync(team.Id, cancellationToken)).Count
            : 0;
        var roleDefinitions = await GetRoleDefinitionsAsync(team.Id, cancellationToken);

        return new TeamDetailResult(
            Team: team,
            Members: activeMembers
                .OrderBy(m => m.Role)
                .ThenBy(m => m.User.DisplayName, StringComparer.OrdinalIgnoreCase)
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
        // Still returns TeamMember entities for callers that need navigation properties (MyTeams page).
        // Uses DB query because TeamMember → Team navigation is needed.
        return await _dbContext.TeamMembers
            .AsNoTracking()
            .Where(tm => tm.UserId == userId && tm.LeftAt == null)
            .Include(tm => tm.Team)
                .ThenInclude(t => t.ParentTeam)
            .OrderBy(tm => tm.Team.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MyTeamMembershipSummary>> GetMyTeamMembershipsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // This is the current user's own My Teams page — show all their memberships including hidden teams
        var allMemberships = await GetUserTeamsAsync(userId, cancellationToken);
        var memberships = allMemberships;
        var isBoardMember = await _roleAssignmentService.IsUserBoardMemberAsync(userId, cancellationToken);
        var isAdmin = await _roleAssignmentService.IsUserAdminAsync(userId, cancellationToken);
        var isTeamsAdmin = await _roleAssignmentService.IsUserTeamsAdminAsync(userId, cancellationToken);

        var coordinatorTeamIds = memberships
            .Where(m => (m.Role == TeamMemberRole.Coordinator || isBoardMember) && !m.Team.IsSystemTeam)
            .Select(m => m.TeamId)
            .ToHashSet();

        // Find child teams of coordinator departments for pending request counts
        var childTeamsByParent = coordinatorTeamIds.Count > 0
            ? await _dbContext.Teams
                .AsNoTracking()
                .Where(t => t.ParentTeamId != null && coordinatorTeamIds.Contains(t.ParentTeamId.Value) && t.IsActive)
                .Select(t => new { t.Id, t.ParentTeamId })
                .ToListAsync(cancellationToken)
            : [];

        var allManageableTeamIds = coordinatorTeamIds
            .Union(childTeamsByParent.Select(c => c.Id))
            .ToList();

        var pendingCounts = allManageableTeamIds.Count > 0
            ? await GetPendingRequestCountsByTeamIdsAsync(allManageableTeamIds, cancellationToken)
            : new Dictionary<Guid, int>();

        return memberships
            .Select(m =>
            {
                var directCount = pendingCounts.GetValueOrDefault(m.TeamId, 0);

                // For coordinator departments, aggregate pending counts from child teams
                var childCount = coordinatorTeamIds.Contains(m.TeamId)
                    ? childTeamsByParent
                        .Where(c => c.ParentTeamId == m.TeamId)
                        .Sum(c => pendingCounts.GetValueOrDefault(c.Id, 0))
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
        CancellationToken cancellationToken = default)
    {
        var team = await _dbContext.Teams.FindAsync(new object[] { teamId }, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
        {
            // System teams only allow description and Google Group prefix changes
            team.Description = description;
            team.GoogleGroupPrefix = googleGroupPrefix;
            team.UpdatedAt = _clock.GetCurrentInstant();
            await _dbContext.SaveChangesAsync(cancellationToken);
            _cache.InvalidateActiveTeams();
            return team;
        }

        if (parentTeamId.HasValue)
        {
            if (parentTeamId.Value == teamId)
                throw new InvalidOperationException("A team cannot be its own parent");

            if (team.IsSystemTeam)
                throw new InvalidOperationException("System teams cannot have parents");

            var hasChildren = await _dbContext.Teams.AnyAsync(t => t.ParentTeamId == teamId && t.IsActive, cancellationToken);
            if (hasChildren)
                throw new InvalidOperationException("This team has sub-teams and cannot become a child of another team");

            var parent = await _dbContext.Teams.FindAsync(new object[] { parentTeamId.Value }, cancellationToken)
                ?? throw new InvalidOperationException($"Parent team {parentTeamId.Value} not found");

            if (parent.IsSystemTeam)
                throw new InvalidOperationException("System teams cannot be parents");

            if (parent.ParentTeamId.HasValue)
                throw new InvalidOperationException("Cannot nest more than one level — the parent team already has a parent");
        }

        // Validate custom slug if provided
        if (!string.IsNullOrWhiteSpace(customSlug))
        {
            var normalized = Helpers.SlugHelper.GenerateSlug(customSlug);
            if (string.IsNullOrEmpty(normalized))
                throw new InvalidOperationException("Custom slug is not valid. Use lowercase letters, numbers, and hyphens.");

            // Check custom slug doesn't collide with another team's Slug or CustomSlug
            var customSlugTaken = await _dbContext.Teams.AnyAsync(
                t => t.Id != teamId && (t.Slug == normalized || t.CustomSlug == normalized), cancellationToken);
            if (customSlugTaken)
                throw new InvalidOperationException($"The slug '{normalized}' is already in use by another team.");

            customSlug = normalized;
        }
        else
        {
            customSlug = null;
        }

        // If team is becoming a sub-team, clear IsManagement and demote coordinators
        var becomingChild = parentTeamId.HasValue && !team.ParentTeamId.HasValue;
        var usersNeedingShiftAuthorizationInvalidation = becomingChild
            ? await _dbContext.Set<TeamRoleAssignment>()
                .Where(a => a.TeamRoleDefinition.TeamId == teamId && a.TeamRoleDefinition.IsManagement)
                .Select(a => a.TeamMember.UserId)
                .Distinct()
                .ToListAsync(cancellationToken)
            : [];

        // Regenerate slug if name changed
        if (!string.Equals(team.Name, name, StringComparison.Ordinal))
        {
            var newSlug = Helpers.SlugHelper.GenerateSlug(name);
            // Check slug isn't taken by another team (also check custom slugs)
            var slugTaken = await _dbContext.Teams.AnyAsync(
                t => t.Id != teamId && (t.Slug == newSlug || t.CustomSlug == newSlug), cancellationToken);
            if (!slugTaken)
            {
                team.Slug = newSlug;
            }
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
        team.UpdatedAt = _clock.GetCurrentInstant();

        if (becomingChild)
        {
            var managementRoles = await _dbContext.Set<TeamRoleDefinition>()
                .Where(d => d.TeamId == teamId && d.IsManagement)
                .ToListAsync(cancellationToken);
            foreach (var role in managementRoles)
            {
                role.IsManagement = false;
                role.UpdatedAt = team.UpdatedAt;
            }

            var coordinators = await _dbContext.TeamMembers
                .Where(m => m.TeamId == teamId && m.LeftAt == null && m.Role == TeamMemberRole.Coordinator)
                .ToListAsync(cancellationToken);
            foreach (var member in coordinators)
            {
                member.Role = TeamMemberRole.Member;
            }

            if (managementRoles.Count > 0 || coordinators.Count > 0)
            {
                _logger.LogInformation("Team {TeamId} became a sub-team: cleared {RoleCount} management roles, demoted {MemberCount} coordinators",
                    teamId, managementRoles.Count, coordinators.Count);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        _cache.InvalidateActiveTeams();
        InvalidateShiftAuthorization(usersNeedingShiftAuthorizationInvalidation);

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
        var team = await _dbContext.Teams.FindAsync(new object[] { teamId }, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (callsToAction.Count > 3)
            throw new InvalidOperationException("A team can have at most 3 calls to action.");

        if (callsToAction.Count(c => c.Style == CallToActionStyle.Primary) > 1)
            throw new InvalidOperationException("Only one primary call to action is allowed.");

        // Only departments (no parent, non-system) can be made public
        if (isPublicPage && (team.IsSystemTeam || team.ParentTeamId.HasValue))
            throw new InvalidOperationException("Only departments (non-system, top-level teams) can be made public.");

        var now = _clock.GetCurrentInstant();
        team.PageContent = pageContent;
        team.CallsToAction = callsToAction;
        team.IsPublicPage = isPublicPage;
        team.ShowCoordinatorsOnPublicPage = showCoordinatorsOnPublicPage;
        team.PageContentUpdatedAt = now;
        team.PageContentUpdatedByUserId = updatedByUserId;
        team.UpdatedAt = now;

        var actor = await _dbContext.Users.FindAsync(new object[] { updatedByUserId }, cancellationToken);
        await _auditLogService.LogAsync(
            AuditAction.TeamPageContentUpdated, nameof(Team), teamId,
            $"Team page content updated. Public: {isPublicPage}",
            updatedByUserId, actor?.DisplayName ?? updatedByUserId.ToString());

        await _dbContext.SaveChangesAsync(cancellationToken);
        TryUpdateCachedTeam(teamId, cachedTeam => cachedTeam with { IsPublicPage = isPublicPage });

        _logger.LogInformation("Team {TeamId} page content updated by {UserId}. Public: {IsPublic}",
            teamId, updatedByUserId, isPublicPage);
    }

    public async Task DeleteTeamAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        var team = await _dbContext.Teams.FindAsync(new object[] { teamId }, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
        {
            throw new InvalidOperationException("Cannot delete system team");
        }

        var hasActiveChildren = await _dbContext.Teams.AnyAsync(t => t.ParentTeamId == teamId && t.IsActive, cancellationToken);
        if (hasActiveChildren)
        {
            throw new InvalidOperationException("Cannot deactivate a team that has active sub-teams. Remove or reassign sub-teams first.");
        }

        team.IsActive = false;
        team.UpdatedAt = _clock.GetCurrentInstant();

        await _dbContext.SaveChangesAsync(cancellationToken);

        RemoveCachedTeam(teamId);

        _logger.LogInformation("Deactivated team {TeamId} ({TeamName})", teamId, team.Name);
    }

    public async Task<TeamJoinRequest> RequestToJoinTeamAsync(
        Guid teamId,
        Guid userId,
        string? message,
        CancellationToken cancellationToken = default)
    {
        var team = await _dbContext.Teams.FindAsync(new object[] { teamId }, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
        {
            throw new InvalidOperationException("Cannot request to join system team");
        }

        if (team.IsHidden)
        {
            throw new InvalidOperationException("Cannot request to join a hidden team");
        }

        if (!team.RequiresApproval)
        {
            throw new InvalidOperationException("This team does not require approval. Use JoinTeamDirectlyAsync instead.");
        }

        // Check for existing pending request
        var existingRequest = await _dbContext.TeamJoinRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.TeamId == teamId && r.UserId == userId && r.Status == TeamJoinRequestStatus.Pending, cancellationToken);

        if (existingRequest is not null)
        {
            throw new InvalidOperationException("User already has a pending request for this team");
        }

        // Check if already a member
        var isMember = await IsUserMemberOfTeamAsync(teamId, userId, cancellationToken);
        if (isMember)
        {
            throw new InvalidOperationException("User is already a member of this team");
        }

        var request = new TeamJoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Message = message,
            RequestedAt = _clock.GetCurrentInstant()
        };

        _dbContext.TeamJoinRequests.Add(request);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("User {UserId} requested to join team {TeamId}", userId, teamId);

        return request;
    }

    public async Task<TeamMember> JoinTeamDirectlyAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var team = await _dbContext.Teams.FindAsync(new object[] { teamId }, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
        {
            throw new InvalidOperationException("Cannot directly join system team");
        }

        if (team.IsHidden)
        {
            throw new InvalidOperationException("Cannot directly join a hidden team");
        }

        if (team.RequiresApproval)
        {
            throw new InvalidOperationException("This team requires approval. Use RequestToJoinTeamAsync instead.");
        }

        // Check if already a member
        var existingMember = await _dbContext.TeamMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(tm => tm.TeamId == teamId && tm.UserId == userId && tm.LeftAt == null, cancellationToken);

        if (existingMember is not null)
        {
            throw new InvalidOperationException("User is already a member of this team");
        }

        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Role = TeamMemberRole.Member,
            JoinedAt = _clock.GetCurrentInstant()
        };

        _dbContext.TeamMembers.Add(member);

        var joiningUser = await _dbContext.Users.FindAsync([userId], cancellationToken);
        await _auditLogService.LogAsync(
            AuditAction.TeamJoinedDirectly, nameof(Team), teamId,
            $"{joiningUser?.DisplayName ?? userId.ToString()} joined {team.Name} directly",
            userId, joiningUser?.DisplayName ?? userId.ToString(),
            relatedEntityId: userId, relatedEntityType: nameof(User));
        EnqueueGoogleSyncOutboxEvent(
            member.Id,
            teamId,
            userId,
            GoogleSyncOutboxEventTypes.AddUserToTeamResources);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to add user {UserId} to team {TeamId} directly", userId, teamId);

            if (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                throw new InvalidOperationException("User is already a member of this team");
            }

            throw;
        }

        _logger.LogInformation("User {UserId} joined team {TeamId} directly", userId, teamId);

        // Update cache
        var joinedUser = joiningUser ?? await _dbContext.Users.FindAsync([userId], cancellationToken);
        if (joinedUser is not null)
        {
            AddMemberToTeamCache(teamId, new CachedTeamMember(
                member.Id, userId, joinedUser.DisplayName, joinedUser.ProfilePictureUrl,
                TeamMemberRole.Member, member.JoinedAt));
        }

        await SendAddedToTeamEmailAsync(userId, team, cancellationToken);

        return member;
    }

    public async Task<bool> LeaveTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var team = await _dbContext.Teams.FindAsync(new object[] { teamId }, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
        {
            throw new InvalidOperationException("Cannot leave system team manually");
        }

        var member = await _dbContext.TeamMembers
            .FirstOrDefaultAsync(tm => tm.TeamId == teamId && tm.UserId == userId && tm.LeftAt == null, cancellationToken);

        if (member is null)
        {
            throw new InvalidOperationException("User is not a member of this team");
        }

        var wasCoordinator = member.Role == TeamMemberRole.Coordinator;

        // Clean up role assignments before departure
        var roleAssignments = await _dbContext.Set<TeamRoleAssignment>()
            .Include(a => a.TeamRoleDefinition)
                .ThenInclude(d => d.Team)
            .Where(a => a.TeamMemberId == member.Id)
            .ToListAsync(cancellationToken);
        if (roleAssignments.Count > 0)
        {
            _dbContext.Set<TeamRoleAssignment>().RemoveRange(roleAssignments);
        }

        member.LeftAt = _clock.GetCurrentInstant();

        var leavingUser = await _dbContext.Users.FindAsync([userId], cancellationToken);
        await _auditLogService.LogAsync(
            AuditAction.TeamLeft, nameof(Team), teamId,
            $"{leavingUser?.DisplayName ?? userId.ToString()} left {team.Name}",
            userId, leavingUser?.DisplayName ?? userId.ToString(),
            relatedEntityId: userId, relatedEntityType: nameof(User));
        EnqueueGoogleSyncOutboxEvent(
            member.Id,
            teamId,
            userId,
            GoogleSyncOutboxEventTypes.RemoveUserFromTeamResources);

        await _dbContext.SaveChangesAsync(cancellationToken);
        RemoveMemberFromTeamCache(teamId, userId);
        InvalidateShiftAuthorizationIfNeeded(userId, roleAssignments);

        _logger.LogInformation("User {UserId} left team {TeamId}", userId, teamId);

        return wasCoordinator;
    }

    public async Task WithdrawJoinRequestAsync(
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var request = await _dbContext.TeamJoinRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && r.UserId == userId, cancellationToken)
            ?? throw new InvalidOperationException("Join request not found");

        request.Withdraw(_clock);

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("User {UserId} withdrew join request {RequestId}", userId, requestId);
    }

    public async Task<TeamMember> ApproveJoinRequestAsync(
        Guid requestId,
        Guid approverUserId,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        var request = await _dbContext.TeamJoinRequests
            .Include(r => r.Team)
            .FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken)
            ?? throw new InvalidOperationException("Join request not found");

        // Verify approver has permission
        var canApprove = await CanUserApproveRequestsForTeamAsync(request.TeamId, approverUserId, cancellationToken);
        if (!canApprove)
        {
            throw new InvalidOperationException("User does not have permission to approve requests for this team");
        }

        request.Approve(approverUserId, notes, _clock);

        // Add as team member
        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = request.TeamId,
            UserId = request.UserId,
            Role = TeamMemberRole.Member,
            JoinedAt = _clock.GetCurrentInstant()
        };

        _dbContext.TeamMembers.Add(member);

        var approver = await _dbContext.Users.FindAsync([approverUserId], cancellationToken);
        await _auditLogService.LogAsync(
            AuditAction.TeamJoinRequestApproved, nameof(Team), request.TeamId,
            $"Join request for {request.Team.Name} approved",
            approverUserId, approver?.DisplayName ?? approverUserId.ToString(),
            relatedEntityId: request.UserId, relatedEntityType: nameof(User));
        EnqueueGoogleSyncOutboxEvent(
            member.Id,
            request.TeamId,
            request.UserId,
            GoogleSyncOutboxEventTypes.AddUserToTeamResources);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to approve join request {RequestId} for user {UserId} to team {TeamId}",
                requestId, request.UserId, request.TeamId);

            if (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                throw new InvalidOperationException("User is already a member of this team");
            }

            throw;
        }

        _logger.LogInformation("Approver {ApproverId} approved join request {RequestId} for user {UserId} to team {TeamId}",
            approverUserId, requestId, request.UserId, request.TeamId);

        // Update cache
        var joinedUser = await _dbContext.Users.FindAsync([request.UserId], cancellationToken);
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
        var request = await _dbContext.TeamJoinRequests
            .FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken)
            ?? throw new InvalidOperationException("Join request not found");

        // Verify approver has permission
        var canApprove = await CanUserApproveRequestsForTeamAsync(request.TeamId, approverUserId, cancellationToken);
        if (!canApprove)
        {
            throw new InvalidOperationException("User does not have permission to reject requests for this team");
        }

        request.Reject(approverUserId, reason, _clock);

        var rejecter = await _dbContext.Users.FindAsync([approverUserId], cancellationToken);
        await _auditLogService.LogAsync(
            AuditAction.TeamJoinRequestRejected, nameof(Team), request.TeamId,
            $"Join request for team rejected: {reason}",
            approverUserId, rejecter?.DisplayName ?? approverUserId.ToString(),
            relatedEntityId: request.UserId, relatedEntityType: nameof(User));

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Approver {ApproverId} rejected join request {RequestId}", approverUserId, requestId);
    }

    public async Task<IReadOnlyList<TeamJoinRequest>> GetPendingRequestsForApproverAsync(
        Guid approverUserId,
        CancellationToken cancellationToken = default)
    {
        var isBoardMember = await _roleAssignmentService.IsUserBoardMemberAsync(approverUserId, cancellationToken);
        var isTeamsAdmin = !isBoardMember && await _roleAssignmentService.IsUserTeamsAdminAsync(approverUserId, cancellationToken);

        // Get teams where user is coordinator (direct teams + child teams of coordinator departments)
        var directLeadTeamIds = await _dbContext.TeamMembers
            .AsNoTracking()
            .Where(tm => tm.UserId == approverUserId && tm.LeftAt == null && tm.Role == TeamMemberRole.Coordinator)
            .Select(tm => tm.TeamId)
            .ToListAsync(cancellationToken);

        // Include child teams of coordinator departments (parent coordinators manage child teams)
        var childTeamIds = directLeadTeamIds.Count > 0
            ? await _dbContext.Teams
                .AsNoTracking()
                .Where(t => t.ParentTeamId != null && directLeadTeamIds.Contains(t.ParentTeamId.Value) && t.IsActive)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken)
            : [];

        var allLeadTeamIds = directLeadTeamIds.Union(childTeamIds).ToList();

        IQueryable<TeamJoinRequest> query = _dbContext.TeamJoinRequests
            .AsNoTracking()
            .Include(r => r.Team)
            .Include(r => r.User)
            .Where(r => r.Status == TeamJoinRequestStatus.Pending);

        if (isBoardMember || isTeamsAdmin)
        {
            // Board members and TeamsAdmins can approve all requests
        }
        else if (allLeadTeamIds.Count > 0)
        {
            query = query.Where(r => allLeadTeamIds.Contains(r.TeamId));
        }
        else
        {
            return [];
        }

        return await query
            .OrderBy(r => r.RequestedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TeamJoinRequest>> GetPendingRequestsForTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.TeamJoinRequests
            .AsNoTracking()
            .Include(r => r.User)
            .Where(r => r.TeamId == teamId && r.Status == TeamJoinRequestStatus.Pending)
            .OrderBy(r => r.RequestedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<TeamJoinRequest?> GetUserPendingRequestAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.TeamJoinRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.TeamId == teamId && r.UserId == userId && r.Status == TeamJoinRequestStatus.Pending, cancellationToken);
    }

    public async Task<bool> CanUserApproveRequestsForTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Admins can approve any team
        var isAdmin = await _roleAssignmentService.IsUserAdminAsync(userId, cancellationToken);
        if (isAdmin)
        {
            return true;
        }

        // Board members can approve any team
        var isBoardMember = await _roleAssignmentService.IsUserBoardMemberAsync(userId, cancellationToken);
        if (isBoardMember)
        {
            return true;
        }

        // TeamsAdmin can approve for any team
        var isTeamsAdmin = await _roleAssignmentService.IsUserTeamsAdminAsync(userId, cancellationToken);
        if (isTeamsAdmin)
        {
            return true;
        }

        // Coordinators can approve their own team
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

        // Check direct coordinator role on this team
        if (team.Members.Any(m => m.UserId == userId && m.Role == TeamMemberRole.Coordinator))
        {
            _logger.LogDebug("Coordinator check: user {UserId} is direct coordinator of team {TeamName} ({TeamId})",
                userId, team.Name, teamId);
            return true;
        }

        // Check IsManagement role assignment (source of truth — handles cases where
        // TeamMember.Role hasn't been reconciled yet)
        var hasManagementRole = await _dbContext.Set<TeamRoleAssignment>()
            .AsNoTracking()
            .AnyAsync(ra =>
                ra.TeamMember.TeamId == teamId &&
                ra.TeamMember.UserId == userId &&
                ra.TeamMember.LeftAt == null &&
                ra.TeamRoleDefinition.IsManagement,
                cancellationToken);
        if (hasManagementRole)
        {
            _logger.LogDebug("Coordinator check: user {UserId} has IsManagement role on team {TeamName} ({TeamId})",
                userId, team.Name, teamId);
            return true;
        }

        // Check if user is coordinator of the parent team (department coordinators manage child teams)
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
        var team = await _dbContext.Teams.FindAsync(new object[] { teamId }, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
        {
            throw new InvalidOperationException("Cannot remove members from system team manually");
        }

        // Verify actor has permission (board member or lead)
        var canApprove = await CanUserApproveRequestsForTeamAsync(teamId, actorUserId, cancellationToken);
        if (!canApprove)
        {
            throw new InvalidOperationException("User does not have permission to remove members from this team");
        }

        var member = await _dbContext.TeamMembers
            .FirstOrDefaultAsync(tm => tm.TeamId == teamId && tm.UserId == userId && tm.LeftAt == null, cancellationToken)
            ?? throw new InvalidOperationException("User is not a member of this team");

        var wasCoordinator = member.Role == TeamMemberRole.Coordinator;

        // Clean up role assignments before departure
        var roleAssignments = await _dbContext.Set<TeamRoleAssignment>()
            .Include(a => a.TeamRoleDefinition)
                .ThenInclude(d => d.Team)
            .Where(a => a.TeamMemberId == member.Id)
            .ToListAsync(cancellationToken);
        if (roleAssignments.Count > 0)
        {
            _dbContext.Set<TeamRoleAssignment>().RemoveRange(roleAssignments);
        }

        member.LeftAt = _clock.GetCurrentInstant();

        var actor = await _dbContext.Users.FindAsync([actorUserId], cancellationToken);
        await _auditLogService.LogAsync(
            AuditAction.TeamMemberRemoved, nameof(Team), teamId,
            $"Member removed from {team.Name}",
            actorUserId, actor?.DisplayName ?? actorUserId.ToString(),
            relatedEntityId: userId, relatedEntityType: nameof(User));
        EnqueueGoogleSyncOutboxEvent(
            member.Id,
            teamId,
            userId,
            GoogleSyncOutboxEventTypes.RemoveUserFromTeamResources);

        await _dbContext.SaveChangesAsync(cancellationToken);
        RemoveMemberFromTeamCache(teamId, userId);
        InvalidateShiftAuthorizationIfNeeded(userId, roleAssignments);

        _logger.LogInformation("Actor {ActorId} removed user {UserId} from team {TeamId}", actorUserId, userId, teamId);

        return wasCoordinator;
    }

    public async Task<TeamMember> AddMemberToTeamAsync(
        Guid teamId,
        Guid targetUserId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var team = await _dbContext.Teams.FindAsync(new object[] { teamId }, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
        {
            throw new InvalidOperationException("Cannot add members to system teams manually");
        }

        // Check no existing active membership
        var existingMember = await _dbContext.TeamMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(tm => tm.TeamId == teamId && tm.UserId == targetUserId && tm.LeftAt == null, cancellationToken);

        if (existingMember is not null)
        {
            throw new InvalidOperationException("User is already a member of this team");
        }

        // Resolve any pending join request for this user
        var pendingRequest = await _dbContext.TeamJoinRequests
            .FirstOrDefaultAsync(r => r.TeamId == teamId && r.UserId == targetUserId
                && r.Status == TeamJoinRequestStatus.Pending, cancellationToken);
        if (pendingRequest is not null)
        {
            pendingRequest.Approve(actorUserId, "Added directly by team manager", _clock);
        }

        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = targetUserId,
            Role = TeamMemberRole.Member,
            JoinedAt = _clock.GetCurrentInstant()
        };

        _dbContext.TeamMembers.Add(member);

        var actor = await _dbContext.Users.FindAsync([actorUserId], cancellationToken);
        await _auditLogService.LogAsync(
            AuditAction.TeamMemberAdded, nameof(Team), teamId,
            $"Member added to {team.Name} by {actor?.DisplayName ?? actorUserId.ToString()}",
            actorUserId, actor?.DisplayName ?? actorUserId.ToString(),
            relatedEntityId: targetUserId, relatedEntityType: nameof(User));
        EnqueueGoogleSyncOutboxEvent(
            member.Id,
            teamId,
            targetUserId,
            GoogleSyncOutboxEventTypes.AddUserToTeamResources);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to add user {UserId} to team {TeamId}", targetUserId, teamId);

            if (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                throw new InvalidOperationException("User is already a member of this team");
            }

            throw;
        }

        _logger.LogInformation("Actor {ActorId} added user {UserId} to team {TeamId}", actorUserId, targetUserId, teamId);

        // Update cache
        var addedUser = await _dbContext.Users.FindAsync([targetUserId], cancellationToken);
        if (addedUser is not null)
        {
            AddMemberToTeamCache(teamId, new CachedTeamMember(
                member.Id, targetUserId, addedUser.DisplayName, addedUser.ProfilePictureUrl,
                TeamMemberRole.Member, member.JoinedAt));
        }

        await SendAddedToTeamEmailAsync(targetUserId, team, cancellationToken);

        return member;
    }

    public async Task<IReadOnlyList<TeamMember>> GetTeamMembersAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.TeamMembers
            .AsNoTracking()
            .Include(tm => tm.User)
            .Where(tm => tm.TeamId == teamId && tm.LeftAt == null)
            .OrderBy(tm => tm.Role)
            .ThenBy(tm => tm.User.DisplayName)
            .ToListAsync(cancellationToken);
    }

    // ==========================================================================
    // Team Role Definitions
    // ==========================================================================

    public async Task<TeamRoleDefinition> CreateRoleDefinitionAsync(
        Guid teamId, string name, string? description, int slotCount,
        List<SlotPriority> priorities, int sortOrder, RolePeriod period, Guid actorUserId,
        bool isPublic = true,
        CancellationToken cancellationToken = default)
    {
        var team = await _dbContext.Teams.FindAsync(new object[] { teamId }, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
        {
            throw new InvalidOperationException("Cannot add role definitions to system teams");
        }

        if (slotCount < 1)
        {
            throw new InvalidOperationException("Slot count must be at least 1");
        }

        if (priorities.Count != slotCount)
        {
            throw new InvalidOperationException($"Priorities count ({priorities.Count}) must match slot count ({slotCount})");
        }

        ValidateRoleName(name);

        // Check name uniqueness within the team (case-insensitive, backed by lower() index)
        var lowerName = name.ToLowerInvariant();
#pragma warning disable MA0011 // EF LINQ: ToLower() translates to SQL lower()
        var nameExists = await _dbContext.Set<TeamRoleDefinition>()
            .AnyAsync(d => d.TeamId == teamId && d.Name.ToLower() == lowerName, cancellationToken);
#pragma warning restore MA0011
        if (nameExists)
        {
            throw new InvalidOperationException($"A role definition with name '{name}' already exists for this team");
        }

        // Verify actor permission
        var canManage = await CanUserApproveRequestsForTeamAsync(teamId, actorUserId, cancellationToken);
        if (!canManage)
        {
            throw new InvalidOperationException("User does not have permission to manage role definitions for this team");
        }

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

        _dbContext.Set<TeamRoleDefinition>().Add(definition);

        var actor = await _dbContext.Users.FindAsync([actorUserId], cancellationToken);
        await _auditLogService.LogAsync(
            AuditAction.TeamRoleDefinitionCreated, nameof(TeamRoleDefinition), definition.Id,
            $"Role definition '{name}' created for team {team.Name}",
            actorUserId, actor?.DisplayName ?? actorUserId.ToString(),
            relatedEntityId: teamId, relatedEntityType: nameof(Team));

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created role definition '{RoleName}' for team {TeamId}", name, teamId);

        return definition;
    }

    public async Task<TeamRoleDefinition> UpdateRoleDefinitionAsync(
        Guid roleDefinitionId, string name, string? description, int slotCount,
        List<SlotPriority> priorities, int sortOrder, bool isManagement, RolePeriod period, Guid actorUserId,
        bool isPublic = true,
        CancellationToken cancellationToken = default)
    {
        var definition = await _dbContext.Set<TeamRoleDefinition>()
            .Include(d => d.Team)
            .Include(d => d.Assignments)
            .FirstOrDefaultAsync(d => d.Id == roleDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException($"Role definition {roleDefinitionId} not found");

        // Verify actor permission
        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
        {
            throw new InvalidOperationException("User does not have permission to manage role definitions for this team");
        }

        if (slotCount < 1)
        {
            throw new InvalidOperationException("Slot count must be at least 1");
        }

        if (priorities.Count != slotCount)
        {
            throw new InvalidOperationException($"Priorities count ({priorities.Count}) must match slot count ({slotCount})");
        }

        // Cannot reduce slot count below filled count
        if (slotCount < definition.Assignments.Count)
        {
            throw new InvalidOperationException(
                $"Cannot reduce slot count to {slotCount} — {definition.Assignments.Count} slots are currently filled");
        }

        // Check name uniqueness if changed
        if (!string.Equals(definition.Name, name, StringComparison.Ordinal))
        {
            ValidateRoleName(name);

            var lowerName = name.ToLowerInvariant();
#pragma warning disable MA0011 // EF LINQ: ToLower() translates to SQL lower()
            var nameExists = await _dbContext.Set<TeamRoleDefinition>()
                .AnyAsync(d => d.TeamId == definition.TeamId && d.Id != roleDefinitionId
                    && d.Name.ToLower() == lowerName, cancellationToken);
#pragma warning restore MA0011
            if (nameExists)
            {
                throw new InvalidOperationException($"A role definition with name '{name}' already exists for this team");
            }
        }

        definition.Name = name;
        definition.Description = description;
        definition.SlotCount = slotCount;
        definition.Priorities = priorities;
        definition.SortOrder = sortOrder;
        var invalidatedActiveTeams = false;
        var usersNeedingShiftAuthorizationInvalidation =
            definition.Team.ParentTeamId is null &&
            definition.Team.SystemTeamType == SystemTeamType.None &&
            definition.IsManagement != isManagement &&
            definition.Assignments.Count > 0
                ? await _dbContext.TeamMembers
                    .Where(m => definition.Assignments.Select(a => a.TeamMemberId).Contains(m.Id))
                    .Select(m => m.UserId)
                    .Distinct()
                    .ToListAsync(cancellationToken)
                : [];

        // If clearing IsManagement, demote any coordinators who have no other management assignments
        if (definition.IsManagement && !isManagement)
        {
            var assignedMemberIds = definition.Assignments.Select(a => a.TeamMemberId).ToList();
            if (assignedMemberIds.Count > 0)
            {
                var members = await _dbContext.TeamMembers
                    .Where(m => assignedMemberIds.Contains(m.Id) && m.Role == TeamMemberRole.Coordinator)
                    .ToListAsync(cancellationToken);

                foreach (var member in members)
                {
                    var hasOtherManagement = await _dbContext.Set<TeamRoleAssignment>()
                        .AnyAsync(a => a.TeamMemberId == member.Id
                            && a.TeamRoleDefinitionId != roleDefinitionId
                            && a.TeamRoleDefinition.IsManagement, cancellationToken);

                    if (!hasOtherManagement)
                    {
                        member.Role = TeamMemberRole.Member;
                        invalidatedActiveTeams = true;
                    }
                }
            }
        }

        definition.IsPublic = isPublic;
        definition.IsManagement = isManagement;
        definition.Period = period;
        definition.UpdatedAt = _clock.GetCurrentInstant();

        var actor = await _dbContext.Users.FindAsync([actorUserId], cancellationToken);
        await _auditLogService.LogAsync(
            AuditAction.TeamRoleDefinitionUpdated, nameof(TeamRoleDefinition), definition.Id,
            $"Role definition '{name}' updated for team {definition.Team.Name}",
            actorUserId, actor?.DisplayName ?? actorUserId.ToString(),
            relatedEntityId: definition.TeamId, relatedEntityType: nameof(Team));

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (invalidatedActiveTeams)
        {
            _cache.InvalidateActiveTeams();
        }

        InvalidateShiftAuthorization(usersNeedingShiftAuthorizationInvalidation);

        _logger.LogInformation("Updated role definition {RoleDefinitionId} '{RoleName}'", roleDefinitionId, name);

        return definition;
    }

    public async Task DeleteRoleDefinitionAsync(
        Guid roleDefinitionId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var definition = await _dbContext.Set<TeamRoleDefinition>()
            .Include(d => d.Team)
            .Include(d => d.Assignments)
            .FirstOrDefaultAsync(d => d.Id == roleDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException($"Role definition {roleDefinitionId} not found");

        if (definition.IsManagement && definition.Assignments.Count > 0)
        {
            throw new InvalidOperationException("Cannot delete the management role while members are assigned to it. Unassign all members first.");
        }

        // Verify actor permission
        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
        {
            throw new InvalidOperationException("User does not have permission to manage role definitions for this team");
        }

        var actor = await _dbContext.Users.FindAsync([actorUserId], cancellationToken);
        await _auditLogService.LogAsync(
            AuditAction.TeamRoleDefinitionDeleted, nameof(TeamRoleDefinition), definition.Id,
            $"Role definition '{definition.Name}' deleted from team {definition.Team.Name}",
            actorUserId, actor?.DisplayName ?? actorUserId.ToString(),
            relatedEntityId: definition.TeamId, relatedEntityType: nameof(Team));

        _dbContext.Set<TeamRoleDefinition>().Remove(definition);

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Deleted role definition {RoleDefinitionId} '{RoleName}' from team {TeamId}",
            roleDefinitionId, definition.Name, definition.TeamId);
    }

    public async Task SetRoleIsManagementAsync(
        Guid roleDefinitionId, bool isManagement, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var definition = await _dbContext.Set<TeamRoleDefinition>()
            .Include(d => d.Team)
            .Include(d => d.Assignments)
            .FirstOrDefaultAsync(d => d.Id == roleDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException($"Role definition {roleDefinitionId} not found");

        // Verify actor permission
        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
        {
            throw new InvalidOperationException("User does not have permission to manage role definitions for this team");
        }

        if (definition.Assignments.Count > 0)
        {
            throw new InvalidOperationException("Cannot change IsManagement while members are assigned to the role");
        }

        if (isManagement)
        {
            // Check no other role in the same team already has IsManagement = true
            var existingManagement = await _dbContext.Set<TeamRoleDefinition>()
                .AnyAsync(d => d.TeamId == definition.TeamId && d.Id != roleDefinitionId && d.IsManagement, cancellationToken);
            if (existingManagement)
            {
                throw new InvalidOperationException("Another role in this team is already marked as the management role");
            }
        }

        definition.IsManagement = isManagement;
        definition.UpdatedAt = _clock.GetCurrentInstant();

        var actor = await _dbContext.Users.FindAsync([actorUserId], cancellationToken);
        await _auditLogService.LogAsync(
            AuditAction.TeamRoleDefinitionUpdated, nameof(TeamRoleDefinition), definition.Id,
            $"IsManagement set to {isManagement} on role '{definition.Name}' in {definition.Team.Name}",
            actorUserId, actor?.DisplayName ?? actorUserId.ToString(),
            relatedEntityId: definition.TeamId, relatedEntityType: nameof(Team));

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Set IsManagement={IsManagement} on role definition {RoleDefinitionId}", isManagement, roleDefinitionId);
    }

    public async Task<IReadOnlyList<TeamRoleDefinition>> GetRoleDefinitionsAsync(
        Guid teamId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Set<TeamRoleDefinition>()
            .Include(d => d.Assignments)
                .ThenInclude(a => a.TeamMember)
                    .ThenInclude(m => m.User)
            .Where(d => d.TeamId == teamId)
            .OrderBy(d => d.SortOrder).ThenBy(d => d.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TeamRoleDefinition>> GetAllRoleDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Set<TeamRoleDefinition>()
            .Include(d => d.Team)
            .Include(d => d.Assignments)
                .ThenInclude(a => a.TeamMember)
                    .ThenInclude(m => m.User)
            .Where(d => d.Team.IsActive && d.Team.SystemTeamType == SystemTeamType.None)
            .OrderBy(d => d.Team.Name).ThenBy(d => d.SortOrder).ThenBy(d => d.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
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
                    assignment?.TeamMember?.User?.DisplayName));
            }
        }

        if (!string.IsNullOrEmpty(priority))
        {
            slots = slots
                .Where(slot => string.Equals(slot.Priority, priority, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (string.Equals(status, "Open", StringComparison.OrdinalIgnoreCase))
        {
            slots = slots.Where(slot => !slot.IsFilled).ToList();
        }
        else if (string.Equals(status, "Filled", StringComparison.OrdinalIgnoreCase))
        {
            slots = slots.Where(slot => slot.IsFilled).ToList();
        }

        if (!string.IsNullOrEmpty(period))
        {
            slots = slots
                .Where(slot => string.Equals(slot.Period, period, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

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
        var definition = await _dbContext.Set<TeamRoleDefinition>()
            .Include(d => d.Team)
            .Include(d => d.Assignments)
            .FirstOrDefaultAsync(d => d.Id == roleDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException($"Role definition {roleDefinitionId} not found");

        // Verify actor permission
        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
        {
            throw new InvalidOperationException("User does not have permission to manage role assignments for this team");
        }

        // Find the team member
        var teamMember = await _dbContext.TeamMembers
            .FirstOrDefaultAsync(tm => tm.TeamId == definition.TeamId && tm.UserId == targetUserId && tm.LeftAt == null, cancellationToken);

        if (teamMember is null)
        {
            // Auto-add to team (inlined to keep everything in one SaveChangesAsync)
            teamMember = new TeamMember
            {
                Id = Guid.NewGuid(),
                TeamId = definition.TeamId,
                UserId = targetUserId,
                Role = TeamMemberRole.Member,
                JoinedAt = _clock.GetCurrentInstant()
            };
            _dbContext.TeamMembers.Add(teamMember);

            // Resolve any pending join request
            var pendingRequest = await _dbContext.TeamJoinRequests
                .FirstOrDefaultAsync(r => r.TeamId == definition.TeamId
                    && r.UserId == targetUserId
                    && r.Status == TeamJoinRequestStatus.Pending, cancellationToken);
            if (pendingRequest is not null)
            {
                pendingRequest.Approve(actorUserId, "Added via role assignment", _clock);
            }

            EnqueueGoogleSyncOutboxEvent(
                teamMember.Id, definition.TeamId, targetUserId,
                GoogleSyncOutboxEventTypes.AddUserToTeamResources);

            var actorForAdd = await _dbContext.Users.FindAsync([actorUserId], cancellationToken);
            await _auditLogService.LogAsync(
                AuditAction.TeamMemberAdded, nameof(Team), definition.TeamId,
                $"Auto-added to {definition.Team.Name} via role assignment",
                actorUserId, actorForAdd?.DisplayName ?? actorUserId.ToString(),
                relatedEntityId: targetUserId, relatedEntityType: nameof(User));
        }

        // Check if already assigned to this role
        if (definition.Assignments.Any(a => a.TeamMemberId == teamMember.Id))
        {
            throw new InvalidOperationException("User is already assigned to this role");
        }

        // Check if slots are available
        if (definition.Assignments.Count >= definition.SlotCount)
        {
            throw new InvalidOperationException($"All {definition.SlotCount} slots for role '{definition.Name}' are filled");
        }

        // Find the next available slot index
        var usedSlots = definition.Assignments.Select(a => a.SlotIndex).ToHashSet();
        var nextSlotIndex = Enumerable.Range(0, definition.SlotCount).First(i => !usedSlots.Contains(i));

        var assignment = new TeamRoleAssignment
        {
            Id = Guid.NewGuid(),
            TeamRoleDefinitionId = roleDefinitionId,
            TeamMemberId = teamMember.Id,
            SlotIndex = nextSlotIndex,
            AssignedAt = _clock.GetCurrentInstant(),
            AssignedByUserId = actorUserId
        };

        _dbContext.Set<TeamRoleAssignment>().Add(assignment);

        // If this is a management role, set TeamMember.Role = Coordinator
        // (Coordinators system team sync is handled by the controller via ISystemTeamSync)
        if (definition.IsManagement && teamMember.Role != TeamMemberRole.Coordinator)
        {
            teamMember.Role = TeamMemberRole.Coordinator;
        }

        var actor = await _dbContext.Users.FindAsync([actorUserId], cancellationToken);
        var targetUser = await _dbContext.Users.FindAsync([targetUserId], cancellationToken);
        await _auditLogService.LogAsync(
            AuditAction.TeamRoleAssigned, nameof(TeamRoleDefinition), roleDefinitionId,
            $"{targetUser?.DisplayName ?? targetUserId.ToString()} assigned to role '{definition.Name}' in {definition.Team.Name}",
            actorUserId, actor?.DisplayName ?? actorUserId.ToString(),
            relatedEntityId: targetUserId, relatedEntityType: nameof(User));

        await _dbContext.SaveChangesAsync(cancellationToken);
        InvalidateShiftAuthorizationIfNeeded(definition, targetUserId);

        // Update cache: if auto-added to team, add member; if promoted to Lead, update role
        if (targetUser is not null)
        {
            var cachedMember = new CachedTeamMember(
                teamMember.Id, targetUserId, targetUser.DisplayName, targetUser.ProfilePictureUrl,
                teamMember.Role, teamMember.JoinedAt);
            // Either add or update depending on whether they were auto-added
            TryUpdateCachedTeam(definition.TeamId, ct =>
            {
                var existing = ct.Members.FirstOrDefault(m => m.UserId == targetUserId);
                return existing is not null
                    ? ct with { Members = ct.Members.Select(m => m.UserId == targetUserId ? cachedMember : m).ToList() }
                    : ct with { Members = [.. ct.Members, cachedMember] };
            });
        }

        _logger.LogInformation("Assigned user {UserId} to role '{RoleName}' (slot {SlotIndex}) in team {TeamId}",
            targetUserId, definition.Name, nextSlotIndex, definition.TeamId);

        return assignment;
    }

    public async Task UnassignFromRoleAsync(
        Guid roleDefinitionId, Guid teamMemberId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var definition = await _dbContext.Set<TeamRoleDefinition>()
            .Include(d => d.Team)
            .FirstOrDefaultAsync(d => d.Id == roleDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException($"Role definition {roleDefinitionId} not found");

        var assignment = await _dbContext.Set<TeamRoleAssignment>()
            .Include(a => a.TeamMember)
                .ThenInclude(m => m.User)
            .FirstOrDefaultAsync(a => a.TeamRoleDefinitionId == roleDefinitionId && a.TeamMemberId == teamMemberId, cancellationToken)
            ?? throw new InvalidOperationException("Assignment not found");

        // Verify actor permission
        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
        {
            throw new InvalidOperationException("User does not have permission to manage role assignments for this team");
        }

        var actor = await _dbContext.Users.FindAsync([actorUserId], cancellationToken);
        await _auditLogService.LogAsync(
            AuditAction.TeamRoleUnassigned, nameof(TeamRoleDefinition), roleDefinitionId,
            $"{assignment.TeamMember.User.DisplayName} unassigned from role '{definition.Name}' in {definition.Team.Name}",
            actorUserId, actor?.DisplayName ?? actorUserId.ToString(),
            relatedEntityId: assignment.TeamMember.UserId, relatedEntityType: nameof(User));

        _dbContext.Set<TeamRoleAssignment>().Remove(assignment);

        // If this is a management role, check if member has remaining management assignments
        if (definition.IsManagement)
        {
            var member = assignment.TeamMember;
            var hasOtherManagementAssignments = await _dbContext.Set<TeamRoleAssignment>()
                .AnyAsync(a => a.TeamMemberId == teamMemberId
                    && a.Id != assignment.Id
                    && a.TeamRoleDefinition.IsManagement, cancellationToken);

            if (!hasOtherManagementAssignments && member.Role == TeamMemberRole.Coordinator)
            {
                member.Role = TeamMemberRole.Member;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        InvalidateShiftAuthorizationIfNeeded(definition, assignment.TeamMember.UserId);

        // Update cache if role changed (Coordinator → Member demotion)
        if (definition.IsManagement)
        {
            UpdateMemberRoleInTeamCache(definition.TeamId, assignment.TeamMember.UserId, assignment.TeamMember.Role);
        }

        _logger.LogInformation("Unassigned team member {TeamMemberId} from role '{RoleName}' in team {TeamId}",
            teamMemberId, definition.Name, definition.TeamId);
    }

    private async Task SendAddedToTeamEmailAsync(Guid userId, Team team, CancellationToken cancellationToken)
    {
        if (team.IsHidden) return;

        try
        {
            var user = await _dbContext.Users
                .Include(u => u.UserEmails)
                .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is null) return;

            var email = user.GetEffectiveEmail() ?? user.Email!;
            var resources = await _dbContext.GoogleResources
                .AsNoTracking()
                .Where(gr => gr.TeamId == team.Id && gr.IsActive)
                .Select(gr => new { gr.Name, gr.Url })
                .ToListAsync(cancellationToken);

            await _emailService.SendAddedToTeamAsync(
                email, user.DisplayName, team.Name, team.Slug,
                resources.Select(r => (r.Name, r.Url)),
                user.PreferredLanguage,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send added-to-team email for user {UserId} team {TeamId}", userId, team.Id);
        }

        // Dispatch in-app notification independently of email success
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

    private void EnqueueGoogleSyncOutboxEvent(
        Guid teamMemberId,
        Guid teamId,
        Guid userId,
        string eventType)
    {
        _dbContext.GoogleSyncOutboxEvents.Add(new GoogleSyncOutboxEvent
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            TeamId = teamId,
            UserId = userId,
            OccurredAt = _clock.GetCurrentInstant(),
            DeduplicationKey = $"{teamMemberId}:{eventType}"
        });
    }

    private static void ValidateRoleName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Role name cannot be empty");
        }

        if (name.Length > 100)
        {
            throw new InvalidOperationException("Role name cannot exceed 100 characters");
        }
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetPendingRequestCountsByTeamIdsAsync(
        IEnumerable<Guid> teamIds,
        CancellationToken cancellationToken = default)
    {
        var teamIdList = teamIds.ToList();
        if (teamIdList.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        var counts = await _dbContext.TeamJoinRequests
            .Where(r => teamIdList.Contains(r.TeamId) && r.Status == TeamJoinRequestStatus.Pending)
            .GroupBy(r => r.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var result = teamIdList.ToDictionary(id => id, _ => 0);
        foreach (var item in counts)
        {
            result[item.TeamId] = item.Count;
        }

        return result;
    }

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

    public async Task<(IReadOnlyList<Team> Items, int TotalCount)> GetAllTeamsForAdminAsync(
        int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Teams
            .Include(t => t.Members.Where(m => m.LeftAt == null))
            .Include(t => t.JoinRequests.Where(r => r.Status == TeamJoinRequestStatus.Pending))
            .Include(t => t.GoogleResources)
            .Include(t => t.RoleDefinitions)
            .OrderBy(t => t.SystemTeamType)
            .ThenBy(t => t.Name);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task<AdminTeamListResult> GetAdminTeamListAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var (items, totalCount) = await GetAllTeamsForAdminAsync(page, pageSize, cancellationToken);

        var activeEventId = await _dbContext.EventSettings
            .Where(e => e.IsActive)
            .OrderBy(e => e.Id)
            .Select(e => e.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var pendingShiftCounts = activeEventId == Guid.Empty
            ? new Dictionary<Guid, int>()
            : await _shiftManagementService.GetPendingShiftSignupCountsByTeamAsync(
                activeEventId, cancellationToken);

        return new AdminTeamListResult(BuildAdminTeamSummaries(items, pendingShiftCounts), totalCount);
    }

    // ==========================================================================
    // Team Cache
    // ==========================================================================

    private async Task<ConcurrentDictionary<Guid, CachedTeam>> GetCachedTeamsAsync(CancellationToken ct = default)
    {
        return await _cache.GetOrCreateAsync(CacheKeys.ActiveTeams, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);

            var teams = await _dbContext.Teams
                .AsNoTracking()
                .Include(t => t.Members.Where(m => m.LeftAt == null))
                    .ThenInclude(m => m.User)
                .Where(t => t.IsActive)
                .ToListAsync(ct);

            return new ConcurrentDictionary<Guid, CachedTeam>(
                teams.ToDictionary(
                    t => t.Id,
                    t => BuildCachedTeam(t)));
        }) ?? new();
    }

    private static CachedTeam BuildCachedTeam(Team team) => new(
        Id: team.Id,
        Name: team.Name,
        Description: team.Description,
        Slug: team.Slug,
        IsSystemTeam: team.IsSystemTeam,
        SystemTeamType: team.SystemTeamType,
        RequiresApproval: team.RequiresApproval,
        IsPublicPage: team.IsPublicPage,
        IsHidden: team.IsHidden,
        CreatedAt: team.CreatedAt,
        Members: team.Members
            .Where(m => m.LeftAt is null)
            .Select(m => new CachedTeamMember(
                TeamMemberId: m.Id,
                UserId: m.UserId,
                DisplayName: m.User.DisplayName,
                ProfilePictureUrl: m.User.ProfilePictureUrl,
                Role: m.Role,
                JoinedAt: m.JoinedAt))
            .ToList(),
        ParentTeamId: team.ParentTeamId);

    private static IReadOnlyList<AdminTeamSummary> BuildAdminTeamSummaries(
        IReadOnlyList<Team> teams,
        IReadOnlyDictionary<Guid, int> pendingShiftCounts)
    {
        var ordered = new List<AdminTeamSummary>(teams.Count);

        foreach (var team in teams)
        {
            if (team.ParentTeamId.HasValue)
            {
                continue;
            }

            ordered.Add(CreateAdminTeamSummary(team, isChildTeam: false, pendingShiftCounts));

            var children = teams
                .Where(child => child.ParentTeamId == team.Id)
                .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase);

            ordered.AddRange(children.Select(child => CreateAdminTeamSummary(child, isChildTeam: true, pendingShiftCounts)));
        }

        return ordered;
    }

    private static AdminTeamSummary CreateAdminTeamSummary(
        Team team, bool isChildTeam, IReadOnlyDictionary<Guid, int> pendingShiftCounts)
    {
        var systemTeamType = team.SystemTeamType != SystemTeamType.None
            ? team.SystemTeamType.ToString()
            : null;
        var hasMailGroup = team.GoogleResources.Any(resource =>
            resource.ResourceType == GoogleResourceType.Group &&
            resource.IsActive);
        var driveResourceCount = team.GoogleResources.Count(resource =>
            resource.ResourceType != GoogleResourceType.Group &&
            resource.IsActive);

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
            hasMailGroup,
            team.GoogleGroupEmail,
            driveResourceCount,
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
        DisplayName: member.User.DisplayName,
        Email: member.User.Email,
        ProfilePictureUrl: member.User.ProfilePictureUrl,
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

    private bool TryMutateActiveTeamsCache(Action<ConcurrentDictionary<Guid, CachedTeam>> mutate) =>
        _cache.TryUpdateExistingValue<ConcurrentDictionary<Guid, CachedTeam>>(CacheKeys.ActiveTeams, mutate);

    private void UpsertCachedTeam(CachedTeam team) =>
        TryMutateActiveTeamsCache(cachedTeams => cachedTeams[team.Id] = team);

    private void RemoveCachedTeam(Guid teamId)
    {
        TryMutateActiveTeamsCache(cachedTeams => cachedTeams.TryRemove(teamId, out _));
    }

    private bool TryUpdateCachedTeam(Guid teamId, Func<CachedTeam, CachedTeam> update)
    {
        var updated = false;

        TryMutateActiveTeamsCache(cachedTeams =>
        {
            if (!cachedTeams.TryGetValue(teamId, out var team))
            {
                return;
            }

            cachedTeams[teamId] = update(team);
            updated = true;
        });

        return updated;
    }

    private void AddMemberToTeamCache(Guid teamId, CachedTeamMember member)
    {
        TryUpdateCachedTeam(teamId, team => team with { Members = [.. team.Members, member] });
    }

    private void RemoveMemberFromTeamCache(Guid teamId, Guid userId)
    {
        TryUpdateCachedTeam(teamId, team => team with { Members = team.Members.Where(m => m.UserId != userId).ToList() });
    }

    private void UpdateMemberRoleInTeamCache(Guid teamId, Guid userId, TeamMemberRole role)
    {
        TryUpdateCachedTeam(teamId, team => team with
        {
            Members = team.Members
                .Select(m => m.UserId == userId ? m with { Role = role } : m)
                .ToList()
        });
    }

    private void InvalidateShiftAuthorizationIfNeeded(Guid userId, IEnumerable<TeamRoleAssignment> roleAssignments)
    {
        if (roleAssignments.Any(IsShiftAuthorizationAssignment))
        {
            _cache.InvalidateShiftAuthorization(userId);
        }
    }

    private void InvalidateShiftAuthorizationIfNeeded(TeamRoleDefinition definition, Guid userId)
    {
        if (IsShiftAuthorizationDefinition(definition))
        {
            _cache.InvalidateShiftAuthorization(userId);
        }
    }

    private void InvalidateShiftAuthorization(IEnumerable<Guid> userIds)
    {
        foreach (var userId in userIds.Distinct())
        {
            _cache.InvalidateShiftAuthorization(userId);
        }
    }

    private static bool IsShiftAuthorizationAssignment(TeamRoleAssignment assignment) =>
        IsShiftAuthorizationDefinition(assignment.TeamRoleDefinition);

    private static bool IsShiftAuthorizationDefinition(TeamRoleDefinition definition) =>
        definition.IsManagement &&
        definition.Team.ParentTeamId is null &&
        definition.Team.SystemTeamType == SystemTeamType.None;

    public void RemoveMemberFromAllTeamsCache(Guid userId)
    {
        TryMutateActiveTeamsCache(cached =>
        {
            foreach (var kvp in cached)
            {
                if (kvp.Value.Members.Any(m => m.UserId == userId))
                {
                    cached[kvp.Key] = kvp.Value with { Members = kvp.Value.Members.Where(m => m.UserId != userId).ToList() };
                }
            }
        });
    }
}
