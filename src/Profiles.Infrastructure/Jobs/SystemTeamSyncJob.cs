using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Profiles.Application.Interfaces;
using Profiles.Domain.Constants;
using Profiles.Domain.Entities;
using Profiles.Domain.Enums;
using Profiles.Infrastructure.Data;

namespace Profiles.Infrastructure.Jobs;

/// <summary>
/// Background job that syncs membership for system-managed teams.
/// </summary>
public class SystemTeamSyncJob
{
    private readonly ProfilesDbContext _dbContext;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly IAuditLogService _auditLogService;
    private readonly ILogger<SystemTeamSyncJob> _logger;
    private readonly IClock _clock;

    public SystemTeamSyncJob(
        ProfilesDbContext dbContext,
        IMembershipCalculator membershipCalculator,
        IGoogleSyncService googleSyncService,
        IAuditLogService auditLogService,
        ILogger<SystemTeamSyncJob> logger,
        IClock clock)
    {
        _dbContext = dbContext;
        _membershipCalculator = membershipCalculator;
        _googleSyncService = googleSyncService;
        _auditLogService = auditLogService;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>
    /// Executes the system team sync job.
    /// </summary>
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting system team sync at {Time}", _clock.GetCurrentInstant());

        try
        {
            // These run sequentially because they share the same DbContext instance,
            // which is not thread-safe. Parallelizing with Task.WhenAll would require
            // IServiceScopeFactory to create separate DbContext instances per task.
            await SyncVolunteersTeamAsync(cancellationToken);
            await SyncMetaleadsTeamAsync(cancellationToken);
            await SyncBoardTeamAsync(cancellationToken);

            _logger.LogInformation("Completed system team sync");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during system team sync");
            throw;
        }
    }

    /// <summary>
    /// Syncs the Volunteers team membership based on document compliance.
    /// Members: All users with all required documents signed.
    /// </summary>
    public async Task SyncVolunteersTeamAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Syncing Volunteers team");

        var team = await GetSystemTeamAsync(SystemTeamType.Volunteers, cancellationToken);
        if (team == null)
        {
            _logger.LogWarning("Volunteers system team not found");
            return;
        }

        // Get all users with profiles that are approved and not suspended
        var allUserIds = await _dbContext.Profiles
            .AsNoTracking()
            .Where(p => !p.IsSuspended && p.IsApproved)
            .Select(p => p.UserId)
            .ToListAsync(cancellationToken);

        // Filter to those with all required consents using batch method to avoid N+1
        var eligibleUserIdSet = await _membershipCalculator.GetUsersWithAllRequiredConsentsAsync(allUserIds, cancellationToken);
        var eligibleUserIds = eligibleUserIdSet.ToList();

        await SyncTeamMembershipAsync(team, eligibleUserIds, cancellationToken);
    }

    /// <summary>
    /// Syncs the Metaleads team membership based on Metalead roles.
    /// Members: All users who are Metalead of any team.
    /// </summary>
    public async Task SyncMetaleadsTeamAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Syncing Metaleads team");

        var team = await GetSystemTeamAsync(SystemTeamType.Metaleads, cancellationToken);
        if (team == null)
        {
            _logger.LogWarning("Metaleads system team not found");
            return;
        }

        // Get all current metaleads (excluding the Metaleads system team itself)
        var metaleadUserIds = await _dbContext.TeamMembers
            .AsNoTracking()
            .Where(tm =>
                tm.LeftAt == null &&
                tm.Role == TeamMemberRole.Metalead &&
                tm.Team.SystemTeamType == SystemTeamType.None) // Only from user-created teams
            .Select(tm => tm.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        await SyncTeamMembershipAsync(team, metaleadUserIds, cancellationToken);
    }

    /// <summary>
    /// Syncs the Board team membership based on RoleAssignment.
    /// Members: All users with active "Board" RoleAssignment.
    /// </summary>
    public async Task SyncBoardTeamAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Syncing Board team");

        var team = await GetSystemTeamAsync(SystemTeamType.Board, cancellationToken);
        if (team == null)
        {
            _logger.LogWarning("Board system team not found");
            return;
        }

        var now = _clock.GetCurrentInstant();

        // Get all users with active Board role assignment
        var boardMemberIds = await _dbContext.RoleAssignments
            .AsNoTracking()
            .Where(ra =>
                ra.RoleName == RoleNames.Board &&
                ra.ValidFrom <= now &&
                (ra.ValidTo == null || ra.ValidTo > now))
            .Select(ra => ra.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        await SyncTeamMembershipAsync(team, boardMemberIds, cancellationToken);
    }

    private async Task<Team?> GetSystemTeamAsync(SystemTeamType systemTeamType, CancellationToken cancellationToken)
    {
        return await _dbContext.Teams
            .Include(t => t.Members.Where(m => m.LeftAt == null))
            .FirstOrDefaultAsync(t => t.SystemTeamType == systemTeamType, cancellationToken);
    }

    private async Task SyncTeamMembershipAsync(Team team, List<Guid> eligibleUserIds, CancellationToken cancellationToken)
    {
        var currentMemberIds = team.Members
            .Where(m => m.LeftAt == null)
            .Select(m => m.UserId)
            .ToHashSet();

        var eligibleSet = eligibleUserIds.ToHashSet();

        // Users to add (in eligible but not current members)
        var toAdd = eligibleSet.Except(currentMemberIds).ToList();

        // Users to remove (current members but not in eligible)
        var toRemove = currentMemberIds.Except(eligibleSet).ToList();

        var now = _clock.GetCurrentInstant();

        // Batch-load display names for affected users (single query)
        var affectedUserIds = toAdd.Concat(toRemove).ToList();
        var userNames = affectedUserIds.Count > 0
            ? await _dbContext.Users
                .AsNoTracking()
                .Where(u => affectedUserIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken)
            : new Dictionary<Guid, string>();

        // Add new members
        foreach (var userId in toAdd)
        {
            var member = new TeamMember
            {
                Id = Guid.NewGuid(),
                TeamId = team.Id,
                UserId = userId,
                Role = TeamMemberRole.Member,
                JoinedAt = now
            };
            _dbContext.TeamMembers.Add(member);

            var userName = userNames.GetValueOrDefault(userId, userId.ToString());
            await _auditLogService.LogAsync(
                AuditAction.TeamMemberAdded, "Team", team.Id,
                $"{userName} added to {team.Name} by system sync",
                nameof(SystemTeamSyncJob),
                relatedEntityId: userId, relatedEntityType: "User");

            await _googleSyncService.AddUserToTeamResourcesAsync(team.Id, userId, cancellationToken);
        }

        // Remove members who are no longer eligible
        foreach (var userId in toRemove)
        {
            var member = team.Members.FirstOrDefault(m => m.UserId == userId && m.LeftAt == null);
            if (member != null)
            {
                member.LeftAt = now;

                var userName = userNames.GetValueOrDefault(userId, userId.ToString());
                await _auditLogService.LogAsync(
                    AuditAction.TeamMemberRemoved, "Team", team.Id,
                    $"{userName} removed from {team.Name} by system sync",
                    nameof(SystemTeamSyncJob),
                    relatedEntityId: userId, relatedEntityType: "User");

                await _googleSyncService.RemoveUserFromTeamResourcesAsync(team.Id, userId, cancellationToken);
            }
        }

        if (toAdd.Count > 0 || toRemove.Count > 0)
        {
            team.UpdatedAt = now;
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Synced {TeamName} team: added {AddCount}, removed {RemoveCount}",
                team.Name, toAdd.Count, toRemove.Count);
        }
    }
}
