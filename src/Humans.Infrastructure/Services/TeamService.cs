using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application;
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
public partial class TeamService : ITeamService
{
    private readonly HumansDbContext _dbContext;
    private readonly IAuditLogService _auditLogService;
    private readonly IEmailService _emailService;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TeamService> _logger;

    public TeamService(
        HumansDbContext dbContext,
        IAuditLogService auditLogService,
        IEmailService emailService,
        IClock clock,
        IMemoryCache cache,
        ILogger<TeamService> logger)
    {
        _dbContext = dbContext;
        _auditLogService = auditLogService;
        _emailService = emailService;
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

        // Retry with incrementing suffix on unique constraint violation
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var slug = attempt == 0 ? baseSlug : $"{baseSlug}-{attempt + 1}";

            var team = new Team
            {
                Id = Guid.NewGuid(),
                Name = name,
                Description = description,
                Slug = slug,
                IsActive = true,
                RequiresApproval = requiresApproval,
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
                // Add to cache
                if (_cache.TryGetValue(CacheKeys.ActiveTeams, out ConcurrentDictionary<Guid, CachedTeam>? cached) && cached != null)
                {
                    cached[team.Id] = new CachedTeam(team.Id, team.Name, team.Description, team.Slug,
                        team.IsSystemTeam, team.SystemTeamType, team.RequiresApproval, team.CreatedAt, [],
                        ParentTeamId: parentTeamId);
                }
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
        return await _dbContext.Teams
            .AsNoTracking()
            .Include(t => t.Members.Where(m => m.LeftAt == null))
                .ThenInclude(m => m.User)
            .Include(t => t.ParentTeam)
            .Include(t => t.ChildTeams)
            .FirstOrDefaultAsync(t => t.Slug == slug, cancellationToken);
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

    public async Task<Team> UpdateTeamAsync(
        Guid teamId,
        string name,
        string? description,
        bool requiresApproval,
        bool isActive,
        Guid? parentTeamId = null,
        string? googleGroupPrefix = null,
        CancellationToken cancellationToken = default)
    {
        var team = await _dbContext.Teams.FindAsync(new object[] { teamId }, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
        {
            throw new InvalidOperationException("Cannot modify system team settings");
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

        // If team is becoming a sub-team, clear IsManagement and demote coordinators
        var becomingChild = parentTeamId.HasValue && !team.ParentTeamId.HasValue;

        // Regenerate slug if name changed
        if (!string.Equals(team.Name, name, StringComparison.Ordinal))
        {
            var newSlug = Helpers.SlugHelper.GenerateSlug(name);
            // Check slug isn't taken by another team
            var slugTaken = await _dbContext.Teams.AnyAsync(
                t => t.Id != teamId && t.Slug == newSlug, cancellationToken);
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

        // Update cache
        if (_cache.TryGetValue(CacheKeys.ActiveTeams, out ConcurrentDictionary<Guid, CachedTeam>? cached) && cached != null)
        {
            if (!isActive)
            {
                cached.TryRemove(teamId, out _);
            }
            else if (cached.TryGetValue(teamId, out var existing))
            {
                cached[teamId] = existing with { Name = name, Description = description, RequiresApproval = requiresApproval, ParentTeamId = parentTeamId };
            }
            else
            {
                // Team reactivated — re-add to cache
                cached[teamId] = BuildCachedTeam(team);
            }
        }

        _logger.LogInformation("Updated team {TeamId} ({TeamName})", teamId, name);

        return team;
    }

    public async Task UpdateTeamPageContentAsync(
        Guid teamId,
        string? pageContent,
        List<CallToAction> callsToAction,
        bool isPublicPage,
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
        team.PageContentUpdatedAt = now;
        team.PageContentUpdatedByUserId = updatedByUserId;
        team.UpdatedAt = now;

        await _dbContext.SaveChangesAsync(cancellationToken);

        var actor = await _dbContext.Users.FindAsync(new object[] { updatedByUserId }, cancellationToken);
        await _auditLogService.LogAsync(
            AuditAction.TeamPageContentUpdated, nameof(Team), teamId,
            $"Team page content updated. Public: {isPublicPage}",
            updatedByUserId, actor?.DisplayName ?? updatedByUserId.ToString());

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

        // Remove from cache
        if (_cache.TryGetValue(CacheKeys.ActiveTeams, out ConcurrentDictionary<Guid, CachedTeam>? cached) && cached != null)
        {
            cached.TryRemove(teamId, out _);
        }

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

        if (!team.RequiresApproval)
        {
            throw new InvalidOperationException("This team does not require approval. Use JoinTeamDirectlyAsync instead.");
        }

        // Check for existing pending request
        var existingRequest = await _dbContext.TeamJoinRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.TeamId == teamId && r.UserId == userId && r.Status == TeamJoinRequestStatus.Pending, cancellationToken);

        if (existingRequest != null)
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

        if (team.RequiresApproval)
        {
            throw new InvalidOperationException("This team requires approval. Use RequestToJoinTeamAsync instead.");
        }

        // Check if already a member
        var existingMember = await _dbContext.TeamMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(tm => tm.TeamId == teamId && tm.UserId == userId && tm.LeftAt == null, cancellationToken);

        if (existingMember != null)
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
        if (joinedUser != null)
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

        if (member == null)
        {
            throw new InvalidOperationException("User is not a member of this team");
        }

        var wasCoordinator = member.Role == TeamMemberRole.Coordinator;

        // Clean up role assignments before departure
        var roleAssignments = await _dbContext.Set<TeamRoleAssignment>()
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
        if (joinedUser != null)
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
        var isBoardMember = await IsUserBoardMemberAsync(approverUserId, cancellationToken);
        var isTeamsAdmin = !isBoardMember && await IsUserTeamsAdminAsync(approverUserId, cancellationToken);

        // Get teams where user is coordinator
        var leadTeamIds = await _dbContext.TeamMembers
            .AsNoTracking()
            .Where(tm => tm.UserId == approverUserId && tm.LeftAt == null && tm.Role == TeamMemberRole.Coordinator)
            .Select(tm => tm.TeamId)
            .ToListAsync(cancellationToken);

        IQueryable<TeamJoinRequest> query = _dbContext.TeamJoinRequests
            .AsNoTracking()
            .Include(r => r.Team)
            .Include(r => r.User)
            .Where(r => r.Status == TeamJoinRequestStatus.Pending);

        if (isBoardMember || isTeamsAdmin)
        {
            // Board members and TeamsAdmins can approve all requests
        }
        else if (leadTeamIds.Count > 0)
        {
            query = query.Where(r => leadTeamIds.Contains(r.TeamId));
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
        var isAdmin = await IsUserAdminAsync(userId, cancellationToken);
        if (isAdmin)
        {
            return true;
        }

        // Board members can approve any team
        var isBoardMember = await IsUserBoardMemberAsync(userId, cancellationToken);
        if (isBoardMember)
        {
            return true;
        }

        // TeamsAdmin can approve for any team
        var isTeamsAdmin = await IsUserTeamsAdminAsync(userId, cancellationToken);
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
            return false;

        // Check direct coordinator role on this team
        if (team.Members.Any(m => m.UserId == userId && m.Role == TeamMemberRole.Coordinator))
            return true;

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
            return true;

        // Check if user is coordinator of the parent team (department coordinators manage child teams)
        if (team.ParentTeamId.HasValue)
            return await IsUserCoordinatorOfTeamAsync(team.ParentTeamId.Value, userId, cancellationToken);

        return false;
    }

    public async Task<bool> IsUserAdminAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        return await _dbContext.RoleAssignments
            .AnyAsync(ra =>
                ra.UserId == userId &&
                ra.RoleName == RoleNames.Admin &&
                ra.ValidFrom <= now &&
                (ra.ValidTo == null || ra.ValidTo > now),
                cancellationToken);
    }

    public async Task<bool> IsUserBoardMemberAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        return await _dbContext.RoleAssignments
            .AnyAsync(ra =>
                ra.UserId == userId &&
                ra.RoleName == RoleNames.Board &&
                ra.ValidFrom <= now &&
                (ra.ValidTo == null || ra.ValidTo > now),
                cancellationToken);
    }

    public async Task<bool> IsUserTeamsAdminAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        return await _dbContext.RoleAssignments
            .AnyAsync(ra =>
                ra.UserId == userId &&
                ra.RoleName == RoleNames.TeamsAdmin &&
                ra.ValidFrom <= now &&
                (ra.ValidTo == null || ra.ValidTo > now),
                cancellationToken);
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

        if (existingMember != null)
        {
            throw new InvalidOperationException("User is already a member of this team");
        }

        // Resolve any pending join request for this user
        var pendingRequest = await _dbContext.TeamJoinRequests
            .FirstOrDefaultAsync(r => r.TeamId == teamId && r.UserId == targetUserId
                && r.Status == TeamJoinRequestStatus.Pending, cancellationToken);
        if (pendingRequest != null)
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
        if (addedUser != null)
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
                    }
                }
            }
        }

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

        if (teamMember == null)
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
            if (pendingRequest != null)
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

        // Update cache: if auto-added to team, add member; if promoted to Lead, update role
        if (targetUser != null)
        {
            var cachedMember = new CachedTeamMember(
                teamMember.Id, targetUserId, targetUser.DisplayName, targetUser.ProfilePictureUrl,
                teamMember.Role, teamMember.JoinedAt);
            // Either add or update depending on whether they were auto-added
            if (_cache.TryGetValue(CacheKeys.ActiveTeams, out ConcurrentDictionary<Guid, CachedTeam>? cachedTeams) && cachedTeams != null
                && cachedTeams.TryGetValue(definition.TeamId, out var ct))
            {
                var existing = ct.Members.FirstOrDefault(m => m.UserId == targetUserId);
                if (existing != null)
                    cachedTeams[definition.TeamId] = ct with { Members = ct.Members.Select(m => m.UserId == targetUserId ? cachedMember : m).ToList() };
                else
                    cachedTeams[definition.TeamId] = ct with { Members = [.. ct.Members, cachedMember] };
            }
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
        try
        {
            var user = await _dbContext.Users
                .Include(u => u.UserEmails)
                .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user == null) return;

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
        if (!RoleNameRegex().IsMatch(name))
        {
            throw new InvalidOperationException(
                "Role name may only contain letters, numbers, spaces, and hyphens");
        }
    }

    [GeneratedRegex(@"^[\p{L}\p{N} \-]+$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RoleNameRegex();

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

        foreach (var team in cached.Values.Where(t => t.SystemTeamType == SystemTeamType.None))
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
        CreatedAt: team.CreatedAt,
        Members: team.Members
            .Where(m => m.LeftAt == null)
            .Select(m => new CachedTeamMember(
                TeamMemberId: m.Id,
                UserId: m.UserId,
                DisplayName: m.User.DisplayName,
                ProfilePictureUrl: m.User.ProfilePictureUrl,
                Role: m.Role,
                JoinedAt: m.JoinedAt))
            .ToList(),
        ParentTeamId: team.ParentTeamId);

    private void AddMemberToTeamCache(Guid teamId, CachedTeamMember member)
    {
        if (_cache.TryGetValue(CacheKeys.ActiveTeams, out ConcurrentDictionary<Guid, CachedTeam>? cached) && cached != null
            && cached.TryGetValue(teamId, out var team))
        {
            cached[teamId] = team with { Members = [.. team.Members, member] };
        }
    }

    private void RemoveMemberFromTeamCache(Guid teamId, Guid userId)
    {
        if (_cache.TryGetValue(CacheKeys.ActiveTeams, out ConcurrentDictionary<Guid, CachedTeam>? cached) && cached != null
            && cached.TryGetValue(teamId, out var team))
        {
            cached[teamId] = team with { Members = team.Members.Where(m => m.UserId != userId).ToList() };
        }
    }

    private void UpdateMemberRoleInTeamCache(Guid teamId, Guid userId, TeamMemberRole role)
    {
        if (_cache.TryGetValue(CacheKeys.ActiveTeams, out ConcurrentDictionary<Guid, CachedTeam>? cached) && cached != null
            && cached.TryGetValue(teamId, out var team))
        {
            cached[teamId] = team with
            {
                Members = team.Members
                    .Select(m => m.UserId == userId ? m with { Role = role } : m)
                    .ToList()
            };
        }
    }

    public void RemoveMemberFromAllTeamsCache(Guid userId)
    {
        if (_cache.TryGetValue(CacheKeys.ActiveTeams, out ConcurrentDictionary<Guid, CachedTeam>? cached) && cached != null)
        {
            foreach (var kvp in cached)
            {
                if (kvp.Value.Members.Any(m => m.UserId == userId))
                {
                    cached[kvp.Key] = kvp.Value with { Members = kvp.Value.Members.Where(m => m.UserId != userId).ToList() };
                }
            }
        }
    }
}
