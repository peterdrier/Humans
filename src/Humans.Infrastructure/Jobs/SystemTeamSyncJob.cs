using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Background job that syncs membership for system-managed teams.
/// </summary>
public class SystemTeamSyncJob
{
    private readonly HumansDbContext _dbContext;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly IAuditLogService _auditLogService;
    private readonly IEmailService _emailService;
    private readonly HumansMetricsService _metrics;
    private readonly ILogger<SystemTeamSyncJob> _logger;
    private readonly IClock _clock;

    public SystemTeamSyncJob(
        HumansDbContext dbContext,
        IMembershipCalculator membershipCalculator,
        IGoogleSyncService googleSyncService,
        IAuditLogService auditLogService,
        IEmailService emailService,
        HumansMetricsService metrics,
        ILogger<SystemTeamSyncJob> logger,
        IClock clock)
    {
        _dbContext = dbContext;
        _membershipCalculator = membershipCalculator;
        _googleSyncService = googleSyncService;
        _auditLogService = auditLogService;
        _emailService = emailService;
        _metrics = metrics;
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
            await SyncLeadsTeamAsync(cancellationToken);
            await SyncBoardTeamAsync(cancellationToken);
            await SyncAsociadosTeamAsync(cancellationToken);
            await SyncColaboradorsTeamAsync(cancellationToken);

            _metrics.RecordJobRun("system_team_sync", "success");
            _logger.LogInformation("Completed system team sync");
        }
        catch (Exception ex)
        {
            _metrics.RecordJobRun("system_team_sync", "failure");
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

        // Filter to those with all required consents for the Volunteers team
        var eligibleUserIdSet = await _membershipCalculator.GetUsersWithAllRequiredConsentsForTeamAsync(
            allUserIds, SystemTeamIds.Volunteers, cancellationToken);
        var eligibleUserIds = eligibleUserIdSet.ToList();

        await SyncTeamMembershipAsync(team, eligibleUserIds, cancellationToken);
    }

    /// <summary>
    /// Syncs the Leads team membership based on Lead roles.
    /// Members: All users who are Lead of any team.
    /// </summary>
    public async Task SyncLeadsTeamAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Syncing Leads team");

        var team = await GetSystemTeamAsync(SystemTeamType.Leads, cancellationToken);
        if (team == null)
        {
            _logger.LogWarning("Leads system team not found");
            return;
        }

        // Get all current leads (excluding the Leads system team itself)
        var leadUserIds = await _dbContext.TeamMembers
            .AsNoTracking()
            .Where(tm =>
                tm.LeftAt == null &&
                tm.Role == TeamMemberRole.Lead &&
                tm.Team.SystemTeamType == SystemTeamType.None) // Only from user-created teams
            .Select(tm => tm.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Additionally filter by Leads-team-required consents
        var eligibleSet = await _membershipCalculator.GetUsersWithAllRequiredConsentsForTeamAsync(
            leadUserIds, SystemTeamIds.Leads, cancellationToken);

        await SyncTeamMembershipAsync(team, eligibleSet.ToList(), cancellationToken);
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

        // Additionally filter by Board-team-required consents
        var eligibleSet = await _membershipCalculator.GetUsersWithAllRequiredConsentsForTeamAsync(
            boardMemberIds, SystemTeamIds.Board, cancellationToken);

        await SyncTeamMembershipAsync(team, eligibleSet.ToList(), cancellationToken);
    }

    /// <summary>
    /// Syncs the Asociados team membership based on approved applications.
    /// Members: All users with an approved Asociado application.
    /// </summary>
    public Task SyncAsociadosTeamAsync(CancellationToken cancellationToken = default) =>
        SyncTierTeamAsync(MembershipTier.Asociado, SystemTeamType.Asociados, SystemTeamIds.Asociados, cancellationToken);

    /// <summary>
    /// Syncs the Colaboradors team membership based on approved Colaborador applications.
    /// Members: All users with an approved Colaborador application who are also in the Volunteers team.
    /// </summary>
    public Task SyncColaboradorsTeamAsync(CancellationToken cancellationToken = default) =>
        SyncTierTeamAsync(MembershipTier.Colaborador, SystemTeamType.Colaboradors, SystemTeamIds.Colaboradors, cancellationToken);

    private async Task SyncTierTeamAsync(MembershipTier tier, SystemTeamType teamType, Guid teamId,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Syncing {TeamType} team", teamType);

        var team = await GetSystemTeamAsync(teamType, cancellationToken);
        if (team == null)
        {
            _logger.LogWarning("{TeamType} system team not found", teamType);
            return;
        }

        var today = _clock.GetCurrentInstant().InUtc().Date;

        var applicationUserIds = await _dbContext.Applications
            .AsNoTracking()
            .Where(a => a.Status == ApplicationStatus.Approved
                && a.MembershipTier == tier
                && (a.TermExpiresAt == null || a.TermExpiresAt >= today))
            .Select(a => a.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Filter by profile status to match per-user sync behavior
        var userIds = await _dbContext.Profiles
            .AsNoTracking()
            .Where(p => applicationUserIds.Contains(p.UserId) && p.IsApproved && !p.IsSuspended)
            .Select(p => p.UserId)
            .ToListAsync(cancellationToken);

        var eligibleSet = await _membershipCalculator.GetUsersWithAllRequiredConsentsForTeamAsync(
            userIds, teamId, cancellationToken);
        var eligibleUserIds = eligibleSet.ToList();

        // Downgrade Profile.MembershipTier for users who no longer have an active approved application for this tier.
        // Before downgrading to Volunteer, check if the user holds an active application for the OTHER higher tier.
        var todayInstant = _clock.GetCurrentInstant();
        var usersWithOtherActiveTier = await _dbContext.Applications
            .AsNoTracking()
            .Where(a => a.Status == ApplicationStatus.Approved
                && a.MembershipTier != tier
                && a.MembershipTier != MembershipTier.Volunteer
                && (a.TermExpiresAt == null || a.TermExpiresAt >= today))
            .Select(a => new { a.UserId, a.MembershipTier })
            .ToListAsync(cancellationToken);
        var otherTierByUser = usersWithOtherActiveTier
            .GroupBy(a => a.UserId)
            .ToDictionary(g => g.Key, g => g.First().MembershipTier);

        var toDowngrade = await _dbContext.Profiles
            .Include(p => p.User)
            .Where(p => p.MembershipTier == tier && !applicationUserIds.Contains(p.UserId))
            .ToListAsync(cancellationToken);

        foreach (var profile in toDowngrade)
        {
            var newTier = otherTierByUser.TryGetValue(profile.UserId, out var otherTier)
                ? otherTier
                : MembershipTier.Volunteer;
            profile.MembershipTier = newTier;
            profile.UpdatedAt = todayInstant;

            await _auditLogService.LogAsync(
                AuditAction.TierDowngraded, "Profile", profile.UserId,
                $"Membership tier changed to {newTier} for {profile.User?.DisplayName ?? "Unknown"} due to {tier} term expiry",
                nameof(SystemTeamSyncJob),
                relatedEntityId: profile.UserId, relatedEntityType: "User");
        }

        if (toDowngrade.Count > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        await SyncTeamMembershipAsync(team, eligibleUserIds, cancellationToken);
    }

    /// <summary>
    /// Syncs Volunteers team membership for a single user. Call this after approving
    /// a volunteer or after they complete their required consents.
    /// </summary>
    public async Task SyncVolunteersMembershipForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var team = await GetSystemTeamAsync(SystemTeamType.Volunteers, cancellationToken);
        if (team == null)
        {
            _logger.LogWarning("Volunteers system team not found");
            return;
        }

        var profile = await _dbContext.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        var isEligible = profile is { IsApproved: true, IsSuspended: false }
            && await _membershipCalculator.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, cancellationToken);

        // Build a single-user eligible list and let the existing sync logic handle add/remove
        var eligibleUserIds = isEligible ? [userId] : new List<Guid>();
        await SyncTeamMembershipAsync(team, eligibleUserIds, cancellationToken, singleUserSync: userId);
    }

    /// <summary>
    /// Syncs Leads team membership for a single user. Call this after changing
    /// a team member's role to/from Lead.
    /// </summary>
    public async Task SyncLeadsMembershipForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var team = await GetSystemTeamAsync(SystemTeamType.Leads, cancellationToken);
        if (team == null)
        {
            _logger.LogWarning("Leads system team not found");
            return;
        }

        // Check if user is currently Lead of any user-created team
        var isLeadAnywhere = await _dbContext.TeamMembers
            .AsNoTracking()
            .AnyAsync(tm =>
                tm.UserId == userId &&
                tm.LeftAt == null &&
                tm.Role == TeamMemberRole.Lead &&
                tm.Team.SystemTeamType == SystemTeamType.None,
                cancellationToken);

        var isEligible = isLeadAnywhere
            && await _membershipCalculator.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Leads, cancellationToken);

        var eligibleUserIds = isEligible ? [userId] : new List<Guid>();
        await SyncTeamMembershipAsync(team, eligibleUserIds, cancellationToken, singleUserSync: userId);
    }

    /// <summary>
    /// Syncs Colaboradors team membership for a single user. Call this after approving
    /// a Colaborador application or after a user's Colaborador status changes.
    /// </summary>
    public Task SyncColaboradorsMembershipForUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        SyncTierMembershipForUserAsync(userId, MembershipTier.Colaborador, SystemTeamType.Colaboradors, SystemTeamIds.Colaboradors, cancellationToken);

    public Task SyncAsociadosMembershipForUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        SyncTierMembershipForUserAsync(userId, MembershipTier.Asociado, SystemTeamType.Asociados, SystemTeamIds.Asociados, cancellationToken);

    private async Task SyncTierMembershipForUserAsync(Guid userId, MembershipTier tier,
        SystemTeamType teamType, Guid teamId, CancellationToken cancellationToken)
    {
        var team = await GetSystemTeamAsync(teamType, cancellationToken);
        if (team == null)
        {
            _logger.LogWarning("{TeamType} system team not found", teamType);
            return;
        }

        var today = _clock.GetCurrentInstant().InUtc().Date;

        var hasApprovedApp = await _dbContext.Applications
            .AsNoTracking()
            .AnyAsync(a =>
                a.UserId == userId &&
                a.Status == ApplicationStatus.Approved &&
                a.MembershipTier == tier &&
                (a.TermExpiresAt == null || a.TermExpiresAt >= today),
                cancellationToken);

        var profile = await _dbContext.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        var isEligible = hasApprovedApp
            && profile is { IsApproved: true, IsSuspended: false }
            && await _membershipCalculator.HasAllRequiredConsentsForTeamAsync(userId, teamId, cancellationToken);

        var eligibleUserIds = isEligible ? [userId] : new List<Guid>();
        await SyncTeamMembershipAsync(team, eligibleUserIds, cancellationToken, singleUserSync: userId);
    }

    private async Task<Team?> GetSystemTeamAsync(SystemTeamType systemTeamType, CancellationToken cancellationToken)
    {
        return await _dbContext.Teams
            .Include(t => t.Members.Where(m => m.LeftAt == null))
            .FirstOrDefaultAsync(t => t.SystemTeamType == systemTeamType, cancellationToken);
    }

    private async Task SyncTeamMembershipAsync(Team team, List<Guid> eligibleUserIds,
        CancellationToken cancellationToken, Guid? singleUserSync = null)
    {
        var currentMemberIds = team.Members
            .Where(m => m.LeftAt == null)
            .Select(m => m.UserId)
            .ToHashSet();

        var eligibleSet = eligibleUserIds.ToHashSet();

        // When syncing a single user, only evaluate that user (don't remove others)
        var scopeIds = singleUserSync.HasValue ? new HashSet<Guid> { singleUserSync.Value } : currentMemberIds.Union(eligibleSet).ToHashSet();

        // Users to add (in eligible but not current members)
        var toAdd = scopeIds.Where(id => eligibleSet.Contains(id) && !currentMemberIds.Contains(id)).ToList();

        // Users to remove (current members but not in eligible)
        var toRemove = scopeIds.Where(id => currentMemberIds.Contains(id) && !eligibleSet.Contains(id)).ToList();

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

        // Send "added to team" emails for newly added members
        if (toAdd.Count > 0)
        {
            var resources = await _dbContext.GoogleResources
                .AsNoTracking()
                .Where(gr => gr.TeamId == team.Id && gr.IsActive)
                .Select(gr => new { gr.Name, gr.Url })
                .ToListAsync(cancellationToken);
            var resourceTuples = resources.Select(r => (r.Name, r.Url)).ToList();

            var addedUsers = await _dbContext.Users
                .Include(u => u.UserEmails)
                .Where(u => toAdd.Contains(u.Id))
                .ToListAsync(cancellationToken);

            foreach (var user in addedUsers)
            {
                try
                {
                    var email = user.GetEffectiveEmail() ?? user.Email!;
                    await _emailService.SendAddedToTeamAsync(
                        email, user.DisplayName, team.Name, team.Slug,
                        resourceTuples, user.PreferredLanguage, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send added-to-team email for user {UserId} team {TeamId}",
                        user.Id, team.Id);
                }
            }
        }
    }
}
