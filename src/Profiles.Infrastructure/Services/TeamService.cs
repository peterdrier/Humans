using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Profiles.Application.Interfaces;
using Profiles.Domain.Constants;
using Profiles.Domain.Entities;
using Profiles.Domain.Enums;
using Profiles.Infrastructure.Data;
using System.Text.RegularExpressions;

namespace Profiles.Infrastructure.Services;

/// <summary>
/// Service for managing teams and team membership.
/// </summary>
public partial class TeamService : ITeamService
{
    private readonly ProfilesDbContext _dbContext;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly IClock _clock;
    private readonly ILogger<TeamService> _logger;

    public TeamService(
        ProfilesDbContext dbContext,
        IGoogleSyncService googleSyncService,
        IClock clock,
        ILogger<TeamService> logger)
    {
        _dbContext = dbContext;
        _googleSyncService = googleSyncService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Team> CreateTeamAsync(
        string name,
        string? description,
        bool requiresApproval,
        CancellationToken cancellationToken = default)
    {
        var slug = GenerateSlug(name);

        // Ensure unique slug
        var existingCount = await _dbContext.Teams.CountAsync(t => t.Slug.StartsWith(slug), cancellationToken);
        if (existingCount > 0)
        {
            slug = $"{slug}-{existingCount + 1}";
        }

        var now = _clock.GetCurrentInstant();
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            Slug = slug,
            IsActive = true,
            RequiresApproval = requiresApproval,
            SystemTeamType = SystemTeamType.None,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.Teams.Add(team);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created team {TeamName} with slug {Slug}", name, slug);

        return team;
    }

    public async Task<Team?> GetTeamBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Teams
            .Include(t => t.Members.Where(m => m.LeftAt == null))
                .ThenInclude(m => m.User)
            .FirstOrDefaultAsync(t => t.Slug == slug, cancellationToken);
    }

    public async Task<Team?> GetTeamByIdAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Teams
            .Include(t => t.Members.Where(m => m.LeftAt == null))
                .ThenInclude(m => m.User)
            .FirstOrDefaultAsync(t => t.Id == teamId, cancellationToken);
    }

    public async Task<IReadOnlyList<Team>> GetAllTeamsAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Teams
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name)
            .Include(t => t.Members.Where(m => m.LeftAt == null))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Team>> GetUserCreatedTeamsAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Teams
            .Where(t => t.IsActive && t.SystemTeamType == SystemTeamType.None)
            .OrderBy(t => t.Name)
            .Include(t => t.Members.Where(m => m.LeftAt == null))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TeamMember>> GetUserTeamsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.TeamMembers
            .Where(tm => tm.UserId == userId && tm.LeftAt == null)
            .Include(tm => tm.Team)
            .OrderBy(tm => tm.Team.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Team> UpdateTeamAsync(
        Guid teamId,
        string name,
        string? description,
        bool requiresApproval,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var team = await _dbContext.Teams.FindAsync(new object[] { teamId }, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
        {
            throw new InvalidOperationException("Cannot modify system team settings");
        }

        team.Name = name;
        team.Description = description;
        team.RequiresApproval = requiresApproval;
        team.IsActive = isActive;
        team.UpdatedAt = _clock.GetCurrentInstant();

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Updated team {TeamId} ({TeamName})", teamId, name);

        return team;
    }

    public async Task DeleteTeamAsync(Guid teamId, CancellationToken cancellationToken = default)
    {
        var team = await _dbContext.Teams.FindAsync(new object[] { teamId }, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
        {
            throw new InvalidOperationException("Cannot delete system team");
        }

        team.IsActive = false;
        team.UpdatedAt = _clock.GetCurrentInstant();

        await _dbContext.SaveChangesAsync(cancellationToken);

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
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Sync Google resources
        await _googleSyncService.AddUserToTeamResourcesAsync(teamId, userId, cancellationToken);

        _logger.LogInformation("User {UserId} joined team {TeamId} directly", userId, teamId);

        return member;
    }

    public async Task LeaveTeamAsync(
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

        member.LeftAt = _clock.GetCurrentInstant();
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Sync Google resources
        await _googleSyncService.RemoveUserFromTeamResourcesAsync(teamId, userId, cancellationToken);

        _logger.LogInformation("User {UserId} left team {TeamId}", userId, teamId);
    }

    public async Task WithdrawJoinRequestAsync(
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var request = await _dbContext.TeamJoinRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && r.UserId == userId, cancellationToken)
            ?? throw new InvalidOperationException("Join request not found");

        if (request.Status != TeamJoinRequestStatus.Pending)
        {
            throw new InvalidOperationException("Can only withdraw pending requests");
        }

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

        if (request.Status != TeamJoinRequestStatus.Pending)
        {
            throw new InvalidOperationException("Can only approve pending requests");
        }

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
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Sync Google resources
        await _googleSyncService.AddUserToTeamResourcesAsync(request.TeamId, request.UserId, cancellationToken);

        _logger.LogInformation("Approver {ApproverId} approved join request {RequestId} for user {UserId} to team {TeamId}",
            approverUserId, requestId, request.UserId, request.TeamId);

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

        if (request.Status != TeamJoinRequestStatus.Pending)
        {
            throw new InvalidOperationException("Can only reject pending requests");
        }

        // Verify approver has permission
        var canApprove = await CanUserApproveRequestsForTeamAsync(request.TeamId, approverUserId, cancellationToken);
        if (!canApprove)
        {
            throw new InvalidOperationException("User does not have permission to reject requests for this team");
        }

        request.Reject(approverUserId, reason, _clock);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Approver {ApproverId} rejected join request {RequestId}", approverUserId, requestId);
    }

    public async Task<IReadOnlyList<TeamJoinRequest>> GetPendingRequestsForApproverAsync(
        Guid approverUserId,
        CancellationToken cancellationToken = default)
    {
        var isBoardMember = await IsUserBoardMemberAsync(approverUserId, cancellationToken);

        // Get teams where user is metalead
        var metaleadTeamIds = await _dbContext.TeamMembers
            .Where(tm => tm.UserId == approverUserId && tm.LeftAt == null && tm.Role == TeamMemberRole.Metalead)
            .Select(tm => tm.TeamId)
            .ToListAsync(cancellationToken);

        IQueryable<TeamJoinRequest> query = _dbContext.TeamJoinRequests
            .Include(r => r.Team)
            .Include(r => r.User)
            .Where(r => r.Status == TeamJoinRequestStatus.Pending);

        if (isBoardMember)
        {
            // Board members can approve all requests
        }
        else if (metaleadTeamIds.Count > 0)
        {
            query = query.Where(r => metaleadTeamIds.Contains(r.TeamId));
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
            .FirstOrDefaultAsync(r => r.TeamId == teamId && r.UserId == userId && r.Status == TeamJoinRequestStatus.Pending, cancellationToken);
    }

    public async Task<bool> CanUserApproveRequestsForTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Board members can approve any team
        var isBoardMember = await IsUserBoardMemberAsync(userId, cancellationToken);
        if (isBoardMember)
        {
            return true;
        }

        // Metaleads can approve their own team
        return await IsUserMetaleadOfTeamAsync(teamId, userId, cancellationToken);
    }

    public async Task<bool> IsUserMemberOfTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.TeamMembers
            .AnyAsync(tm => tm.TeamId == teamId && tm.UserId == userId && tm.LeftAt == null, cancellationToken);
    }

    public async Task<bool> IsUserMetaleadOfTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.TeamMembers
            .AnyAsync(tm => tm.TeamId == teamId && tm.UserId == userId && tm.LeftAt == null && tm.Role == TeamMemberRole.Metalead, cancellationToken);
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

    public async Task SetMemberRoleAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var team = await _dbContext.Teams.FindAsync(new object[] { teamId }, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
        {
            throw new InvalidOperationException("Cannot change roles in system team");
        }

        // Verify actor has permission (board member or metalead)
        var canApprove = await CanUserApproveRequestsForTeamAsync(teamId, actorUserId, cancellationToken);
        if (!canApprove)
        {
            throw new InvalidOperationException("User does not have permission to change roles in this team");
        }

        var member = await _dbContext.TeamMembers
            .FirstOrDefaultAsync(tm => tm.TeamId == teamId && tm.UserId == userId && tm.LeftAt == null, cancellationToken)
            ?? throw new InvalidOperationException("User is not a member of this team");

        member.Role = role;
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Actor {ActorId} set user {UserId} role to {Role} in team {TeamId}",
            actorUserId, userId, role, teamId);
    }

    public async Task RemoveMemberAsync(
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

        // Verify actor has permission (board member or metalead)
        var canApprove = await CanUserApproveRequestsForTeamAsync(teamId, actorUserId, cancellationToken);
        if (!canApprove)
        {
            throw new InvalidOperationException("User does not have permission to remove members from this team");
        }

        var member = await _dbContext.TeamMembers
            .FirstOrDefaultAsync(tm => tm.TeamId == teamId && tm.UserId == userId && tm.LeftAt == null, cancellationToken)
            ?? throw new InvalidOperationException("User is not a member of this team");

        member.LeftAt = _clock.GetCurrentInstant();
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Sync Google resources
        await _googleSyncService.RemoveUserFromTeamResourcesAsync(teamId, userId, cancellationToken);

        _logger.LogInformation("Actor {ActorId} removed user {UserId} from team {TeamId}", actorUserId, userId, teamId);
    }

    public async Task<IReadOnlyList<TeamMember>> GetTeamMembersAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.TeamMembers
            .Include(tm => tm.User)
            .Where(tm => tm.TeamId == teamId && tm.LeftAt == null)
            .OrderBy(tm => tm.Role)
            .ThenBy(tm => tm.JoinedAt)
            .ToListAsync(cancellationToken);
    }

    private static string GenerateSlug(string name)
    {
        var slug = name.ToLowerInvariant();
        slug = SlugRegex().Replace(slug, "-");
        slug = MultipleHyphensRegex().Replace(slug, "-");
        slug = slug.Trim('-');
        return slug;
    }

    [GeneratedRegex(@"[^a-z0-9\-]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"-+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MultipleHyphensRegex();
}
