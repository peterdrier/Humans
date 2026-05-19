using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Jobs;

/// <summary>Background job that syncs membership for system-managed teams.</summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
public class SystemTeamSyncJob(
    ITeamService teamService,
    IUserService userService,
    IUserEmailService userEmailService,
    ICampRepository campRepository,
    IServiceProvider serviceProvider,
    IGoogleSyncService googleSyncService,
    IGoogleGroupSync googleGroupSync,
    IAuditLogService auditLogService,
    IEmailService emailService,
    IRoleAssignmentClaimsCacheInvalidator roleAssignmentClaimsInvalidator,
    IHumansMetrics metrics,
    ILogger<SystemTeamSyncJob> logger,
    IClock clock) : ISystemTeamSync
{
    private readonly IUserEmailService _userEmailService = userEmailService;

    // Lazy via IServiceProvider to break DI cycles (these services depend on ISystemTeamSync).

    private IApplicationDecisionService ApplicationDecisionService =>
        serviceProvider.GetRequiredService<IApplicationDecisionService>();

    private IRoleAssignmentService RoleAssignmentService =>
        serviceProvider.GetRequiredService<IRoleAssignmentService>();

    private IProfileService ProfileService =>
        serviceProvider.GetRequiredService<IProfileService>();

    private ITeamResourceService TeamResourceService =>
        serviceProvider.GetRequiredService<ITeamResourceService>();

    private IMembershipCalculator MembershipCalculator =>
        serviceProvider.GetRequiredService<IMembershipCalculator>();

    /// <summary>
    /// Executes the system team sync job and returns a report of what changed.
    /// </summary>
    public async Task<SyncReport> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting system team sync at {Time}", clock.GetCurrentInstant());
        var report = new SyncReport();

        try
        {
            // Sequential — coordinator reconciliation must land before Coordinators sync.
            await SyncVolunteersTeamAsync(report, cancellationToken);
            await ReconcileCoordinatorRolesAsync(report, cancellationToken);
            await SyncCoordinatorsTeamAsync(report, cancellationToken);
            await SyncBoardTeamAsync(report, cancellationToken);
            await SyncAsociadosTeamAsync(report, cancellationToken);
            await SyncColaboradorsTeamAsync(report, cancellationToken);
            await SyncBarrioLeadsTeamAsync(report, cancellationToken);

            await googleGroupSync.ReconcileAllAsync(SyncAction.Execute, cancellationToken);

            metrics.RecordJobRun("system_team_sync", "success");
            logger.LogInformation("Completed system team sync");
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("system_team_sync", "failure");
            logger.LogError(ex, "Error during system team sync");
            throw;
        }

        return report;
    }

    /// <summary>
    /// Reconciles TeamMember.Role with IsManagement role assignments.
    /// Members assigned to an IsManagement role definition should have Role = Coordinator.
    /// Members not assigned to any IsManagement role should have Role = Member.
    /// </summary>
    public async Task ReconcileCoordinatorRolesAsync(SyncReport? report = null, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Reconciling coordinator roles with IsManagement assignments");
        var step = new SyncStepResult("Coordinator Role Reconciliation");

        var memberships = await teamService
            .GetActiveMembershipsForRoleReconciliationAsync(cancellationToken);

        var shouldBeCoordinator = memberships
            .Where(tm =>
                tm.Role == TeamMemberRole.Member &&
                tm.HasManagementRoleAssignment)
            .ToList();

        var shouldBeMember = memberships
            .Where(tm =>
                tm.Role == TeamMemberRole.Coordinator &&
                tm.SystemTeamType == SystemTeamType.None &&
                !tm.HasManagementRoleAssignment)
            .ToList();

        if (shouldBeCoordinator.Count == 0 && shouldBeMember.Count == 0)
        {
            report?.Steps.Add(step);
            return;
        }

        var affectedUserIds = shouldBeCoordinator.Select(tm => tm.UserId)
            .Concat(shouldBeMember.Select(tm => tm.UserId))
            .Distinct()
            .ToList();
        var userNamesById = await userService.GetUserInfosAsync(affectedUserIds, cancellationToken);

        var changes = new List<(Guid TeamMemberId, TeamMemberRole Role)>(
            shouldBeCoordinator.Count + shouldBeMember.Count);

        foreach (var member in shouldBeCoordinator)
        {
            changes.Add((member.TeamMemberId, TeamMemberRole.Coordinator));
            var userName = userNamesById.TryGetValue(member.UserId, out var u)
                ? u.BurnerName : member.UserId.ToString();
            var teamName = member.TeamName;
            step.Fixed(member.UserId, userName, $"Promoted to Coordinator on {teamName}");
            logger.LogInformation(
                "Reconciled {UserName} to Coordinator on team {TeamId} (had IsManagement role assignment)",
                userName, member.TeamId);
        }

        foreach (var member in shouldBeMember)
        {
            changes.Add((member.TeamMemberId, TeamMemberRole.Member));
            var userName = userNamesById.TryGetValue(member.UserId, out var u)
                ? u.BurnerName : member.UserId.ToString();
            var teamName = member.TeamName;
            step.Fixed(member.UserId, userName, $"Demoted to Member on {teamName} (no IsManagement role)");
            logger.LogInformation(
                "Reconciled {UserName} to Member on team {TeamId} (no IsManagement role assignment)",
                userName, member.TeamId);
        }

        await teamService.ApplyMemberRoleChangesAsync(changes, cancellationToken);

        report?.Steps.Add(step);
    }

    /// <summary>
    /// Syncs the Volunteers team membership based on document compliance.
    /// Members: All users with all required documents signed.
    /// </summary>
    public async Task SyncVolunteersTeamAsync(SyncReport? report = null, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Syncing Volunteers team");
        var step = new SyncStepResult("Volunteers");

        var team = (await teamService.GetTeamsAsync(cancellationToken)).Values
            .Where(t => t.SystemTeamType == SystemTeamType.Volunteers)
            .Select(t => new SystemTeamMembershipSnapshot(
                t.Id, t.Name, t.Slug, t.IsHidden, t.SystemTeamType,
                t.Members.Select(m => m.UserId).ToList()))
            .FirstOrDefault();
        if (team is null)
        {
            logger.LogWarning("Volunteers system team not found");
            report?.Steps.Add(step);
            return;
        }

        // Volunteers admission does NOT require Profile.IsApproved — Flagged + RejectedAt are the CC's kick-out levers.
        var candidateIds = (await userService.GetAllUserInfosAsync(cancellationToken).ConfigureAwait(false))
            .Where(u => u.Profile is not null
                && !u.IsSuspended
                && u.Profile.ConsentCheckStatus != ConsentCheckStatus.Flagged
                && u.Profile.RejectedAt is null)
            .Select(u => u.Id)
            .ToList();

        var eligibleSet = await MembershipCalculator.GetUsersWithAllRequiredConsentsForTeamAsync(
            candidateIds, SystemTeamIds.Volunteers, cancellationToken);

        await SyncTeamMembershipAsync(team, eligibleSet.ToList(), cancellationToken, step: step);
        report?.Steps.Add(step);
    }

    /// <summary>
    /// Syncs the Coordinators team membership based on Coordinator roles.
    /// Members: All users who are Coordinator of any team.
    /// </summary>
    public async Task SyncCoordinatorsTeamAsync(SyncReport? report = null, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Syncing Coordinators team");
        var step = new SyncStepResult("Coordinators");

        var team = (await teamService.GetTeamsAsync(cancellationToken)).Values
            .Where(t => t.SystemTeamType == SystemTeamType.Coordinators)
            .Select(t => new SystemTeamMembershipSnapshot(
                t.Id, t.Name, t.Slug, t.IsHidden, t.SystemTeamType,
                t.Members.Select(m => m.UserId).ToList()))
            .FirstOrDefault();
        if (team is null)
        {
            logger.LogWarning("Coordinators system team not found");
            report?.Steps.Add(step);
            return;
        }

        // Department-level only (sub-team managers excluded).
        var teamsById = await teamService.GetTeamsAsync(cancellationToken);
        var leadUserIds = teamsById.Values
            .Where(t => t.IsActive && !t.IsSystemTeam && t.ParentTeamId is null)
            .SelectMany(t => t.Members.Where(m => m.Role == TeamMemberRole.Coordinator).Select(m => m.UserId))
            .Distinct()
            .ToList();

        var eligibleSet = await MembershipCalculator.GetUsersWithAllRequiredConsentsForTeamAsync(
            leadUserIds, SystemTeamIds.Coordinators, cancellationToken);

        await SyncTeamMembershipAsync(team, eligibleSet.ToList(), cancellationToken, step: step);
        report?.Steps.Add(step);
    }

    /// <summary>
    /// Syncs the Board team membership based on RoleAssignment.
    /// Members: All users with active "Board" RoleAssignment.
    /// </summary>
    public async Task SyncBoardTeamAsync(SyncReport? report = null, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Syncing Board team");
        var step = new SyncStepResult("Board");

        var team = (await teamService.GetTeamsAsync(cancellationToken)).Values
            .Where(t => t.SystemTeamType == SystemTeamType.Board)
            .Select(t => new SystemTeamMembershipSnapshot(
                t.Id, t.Name, t.Slug, t.IsHidden, t.SystemTeamType,
                t.Members.Select(m => m.UserId).ToList()))
            .FirstOrDefault();
        if (team is null)
        {
            logger.LogWarning("Board system team not found");
            report?.Steps.Add(step);
            return;
        }

        var boardMemberIds = await RoleAssignmentService.GetActiveUserIdsInRoleAsync(
            RoleNames.Board, cancellationToken);

        var eligibleSet = await MembershipCalculator.GetUsersWithAllRequiredConsentsForTeamAsync(
            boardMemberIds, SystemTeamIds.Board, cancellationToken);

        await SyncTeamMembershipAsync(team, eligibleSet.ToList(), cancellationToken, step: step);
        report?.Steps.Add(step);
    }

    /// <summary>
    /// Syncs the Asociados team membership based on approved applications.
    /// Members: All users with an approved Asociado application.
    /// </summary>
    public Task SyncAsociadosTeamAsync(SyncReport? report = null, CancellationToken cancellationToken = default) =>
        SyncTierTeamAsync(MembershipTier.Asociado, SystemTeamType.Asociados, SystemTeamIds.Asociados, report, cancellationToken);

    /// <summary>
    /// Syncs the Colaboradors team membership based on approved Colaborador applications.
    /// Members: All users with an approved Colaborador application who are also in the Volunteers team.
    /// </summary>
    public Task SyncColaboradorsTeamAsync(SyncReport? report = null, CancellationToken cancellationToken = default) =>
        SyncTierTeamAsync(MembershipTier.Colaborador, SystemTeamType.Colaboradors, SystemTeamIds.Colaboradors, report, cancellationToken);

    private async Task SyncTierTeamAsync(MembershipTier tier, SystemTeamType teamType, Guid teamId,
        SyncReport? report, CancellationToken cancellationToken)
    {
        logger.LogDebug("Syncing {TeamType} team", teamType);
        var step = new SyncStepResult(teamType.ToString());

        var team = (await teamService.GetTeamsAsync(cancellationToken)).Values
            .Where(t => t.SystemTeamType == teamType)
            .Select(t => new SystemTeamMembershipSnapshot(
                t.Id, t.Name, t.Slug, t.IsHidden, t.SystemTeamType,
                t.Members.Select(m => m.UserId).ToList()))
            .FirstOrDefault();
        if (team is null)
        {
            logger.LogWarning("{TeamType} system team not found", teamType);
            report?.Steps.Add(step);
            return;
        }

        var today = clock.GetCurrentInstant().InUtc().Date;

        var applicationUserIds = await ApplicationDecisionService
            .GetActiveApprovedTierUserIdsAsync(tier, today, cancellationToken);

        var activeSet = (await userService.GetAllUserInfosAsync(cancellationToken).ConfigureAwait(false))
            .Where(u => u.IsActive)
            .Select(u => u.Id)
            .ToHashSet();
        var userIds = applicationUserIds.Where(activeSet.Contains).ToList();

        var eligibleSet = await MembershipCalculator.GetUsersWithAllRequiredConsentsForTeamAsync(
            userIds, teamId, cancellationToken);
        var eligibleUserIds = eligibleSet.ToList();

        // Downgrade tier for users whose approved application expired; honor a still-active other-tier application.
        var todayInstant = clock.GetCurrentInstant();
        var otherTierByUser = await ApplicationDecisionService
            .GetOtherActiveTierAssignmentsAsync(tier, today, cancellationToken);

        var downgrades = await ProfileService.DowngradeTierForExpiredAsync(
            tier, applicationUserIds, otherTierByUser, todayInstant, cancellationToken);

        if (downgrades.Count > 0)
        {
            var downgradeUserIds = downgrades.Select(d => d.UserId).ToList();
            var downgradeUsersById = await userService.GetUserInfosAsync(downgradeUserIds, cancellationToken);

            foreach (var (downgradeUserId, newTier) in downgrades)
            {
                var displayName = downgradeUsersById.TryGetValue(downgradeUserId, out var u)
                    ? u.BurnerName : "Unknown";
                await auditLogService.LogAsync(
                    AuditAction.TierDowngraded, nameof(Profile), downgradeUserId,
                    $"Membership tier changed to {newTier} for {displayName} due to {tier} term expiry",
                    nameof(SystemTeamSyncJob),
                    relatedEntityId: downgradeUserId, relatedEntityType: nameof(User));
            }
        }

        await SyncTeamMembershipAsync(team, eligibleUserIds, cancellationToken, step: step);
        report?.Steps.Add(step);
    }

    public Task SyncMembershipForUserAsync(
        Guid userId,
        SystemTeamType teamType,
        CancellationToken cancellationToken = default) =>
        teamType switch
        {
            SystemTeamType.Volunteers => SyncVolunteersMembershipForUserAsync(userId, cancellationToken),
            SystemTeamType.Coordinators => SyncCoordinatorsMembershipForUserAsync(userId, cancellationToken),
            SystemTeamType.Colaboradors => SyncTierMembershipForUserAsync(
                userId, MembershipTier.Colaborador, SystemTeamType.Colaboradors, SystemTeamIds.Colaboradors, cancellationToken),
            SystemTeamType.Asociados => SyncTierMembershipForUserAsync(
                userId, MembershipTier.Asociado, SystemTeamType.Asociados, SystemTeamIds.Asociados, cancellationToken),
            SystemTeamType.BarrioLeads => SyncBarrioLeadsMembershipForUserAsync(userId, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(teamType), teamType, "System team does not support per-user sync.")
        };

    /// <summary>
    /// Syncs Volunteers team membership for a single user. Call this after approving
    /// a volunteer or after they complete their required consents.
    /// </summary>
    private async Task SyncVolunteersMembershipForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var team = (await teamService.GetTeamsAsync(cancellationToken)).Values
            .Where(t => t.SystemTeamType == SystemTeamType.Volunteers)
            .Select(t => new SystemTeamMembershipSnapshot(
                t.Id, t.Name, t.Slug, t.IsHidden, t.SystemTeamType,
                t.Members.Select(m => m.UserId).ToList()))
            .FirstOrDefault();
        if (team is null)
        {
            logger.LogWarning("Volunteers system team not found");
            return;
        }

        var profile = (await userService.GetUserInfoAsync(userId, cancellationToken))?.Profile;

        // Volunteers admission does NOT require IsApproved — see SyncVolunteersTeamAsync.
        var isEligible = profile is { RejectedAt: null, State: not ProfileState.Suspended }
            && profile.ConsentCheckStatus != ConsentCheckStatus.Flagged
            && await MembershipCalculator.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, cancellationToken);

        var eligibleUserIds = isEligible ? [userId] : new List<Guid>();
        await SyncTeamMembershipAsync(team, eligibleUserIds, cancellationToken, singleUserSync: userId);
    }

    /// <summary>
    /// Syncs Coordinators team membership for a single user. Call this after changing
    /// a team member's role to/from Coordinator.
    /// </summary>
    private async Task SyncCoordinatorsMembershipForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var team = (await teamService.GetTeamsAsync(cancellationToken)).Values
            .Where(t => t.SystemTeamType == SystemTeamType.Coordinators)
            .Select(t => new SystemTeamMembershipSnapshot(
                t.Id, t.Name, t.Slug, t.IsHidden, t.SystemTeamType,
                t.Members.Select(m => m.UserId).ToList()))
            .FirstOrDefault();
        if (team is null)
        {
            logger.LogWarning("Coordinators system team not found");
            return;
        }

        var teamsById = await teamService.GetTeamsAsync(cancellationToken);
        var isCoordinatorAnywhere = teamsById.Values
            .Where(t => t.IsActive && !t.IsSystemTeam && t.ParentTeamId is null)
            .Any(t => t.Members.Any(m => m.UserId == userId && m.Role == TeamMemberRole.Coordinator));

        var isEligible = isCoordinatorAnywhere
            && await MembershipCalculator.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Coordinators, cancellationToken);

        var eligibleUserIds = isEligible ? [userId] : new List<Guid>();
        await SyncTeamMembershipAsync(team, eligibleUserIds, cancellationToken, singleUserSync: userId);
    }

    /// <summary>
    /// Syncs Colaboradors team membership for a single user. Call this after approving
    /// a Colaborador application or after a user's Colaborador status changes.
    /// </summary>
    private async Task SyncTierMembershipForUserAsync(Guid userId, MembershipTier tier,
        SystemTeamType teamType, Guid teamId, CancellationToken cancellationToken)
    {
        var team = (await teamService.GetTeamsAsync(cancellationToken)).Values
            .Where(t => t.SystemTeamType == teamType)
            .Select(t => new SystemTeamMembershipSnapshot(
                t.Id, t.Name, t.Slug, t.IsHidden, t.SystemTeamType,
                t.Members.Select(m => m.UserId).ToList()))
            .FirstOrDefault();
        if (team is null)
        {
            logger.LogWarning("{TeamType} system team not found", teamType);
            return;
        }

        var today = clock.GetCurrentInstant().InUtc().Date;

        var hasApprovedApp = await ApplicationDecisionService
            .HasActiveApprovedTierAsync(userId, tier, today, cancellationToken);

        var profile = (await userService.GetUserInfoAsync(userId, cancellationToken))?.Profile;

        var isEligible = hasApprovedApp
            && profile is { IsApproved: true, State: not ProfileState.Suspended }
            && await MembershipCalculator.HasAllRequiredConsentsForTeamAsync(userId, teamId, cancellationToken);

        var eligibleUserIds = isEligible ? [userId] : new List<Guid>();
        await SyncTeamMembershipAsync(team, eligibleUserIds, cancellationToken, singleUserSync: userId);
    }

    /// <summary>
    /// Syncs the Barrio Leads team membership based on active CampLead assignments.
    /// Members: All users who are active leads of any camp.
    /// </summary>
    public async Task SyncBarrioLeadsTeamAsync(SyncReport? report = null, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Syncing Barrio Leads team");
        var step = new SyncStepResult("Barrio Leads");

        var team = (await teamService.GetTeamsAsync(cancellationToken)).Values
            .Where(t => t.SystemTeamType == SystemTeamType.BarrioLeads)
            .Select(t => new SystemTeamMembershipSnapshot(
                t.Id, t.Name, t.Slug, t.IsHidden, t.SystemTeamType,
                t.Members.Select(m => m.UserId).ToList()))
            .FirstOrDefault();
        if (team is null)
        {
            logger.LogWarning("Barrio Leads system team not found");
            report?.Steps.Add(step);
            return;
        }

        var activeLeadUserIds = await campRepository.GetActiveLeadUserIdsAsync(cancellationToken);
        var eligibleUserIds = activeLeadUserIds.ToList();

        await SyncTeamMembershipAsync(team, eligibleUserIds, cancellationToken, step: step);
        report?.Steps.Add(step);
    }

    /// <summary>
    /// Syncs Barrio Leads team membership for a single user. Call this after adding
    /// or removing a camp lead assignment.
    /// </summary>
    private async Task SyncBarrioLeadsMembershipForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var team = (await teamService.GetTeamsAsync(cancellationToken)).Values
            .Where(t => t.SystemTeamType == SystemTeamType.BarrioLeads)
            .Select(t => new SystemTeamMembershipSnapshot(
                t.Id, t.Name, t.Slug, t.IsHidden, t.SystemTeamType,
                t.Members.Select(m => m.UserId).ToList()))
            .FirstOrDefault();
        if (team is null)
        {
            logger.LogWarning("Barrio Leads system team not found");
            return;
        }

        var isLeadAnywhere = await campRepository.IsLeadAnywhereAsync(userId, cancellationToken);

        // Idempotency guard — avoids IX_team_members_active_unique violation when re-registering.
        if (isLeadAnywhere)
        {
            var alreadyActive = team.ActiveMemberUserIds.Contains(userId);
            if (alreadyActive)
            {
                return;
            }
        }

        var eligibleUserIds = isLeadAnywhere ? [userId] : new List<Guid>();
        await SyncTeamMembershipAsync(team, eligibleUserIds, cancellationToken, singleUserSync: userId);
    }

    private async Task SyncTeamMembershipAsync(SystemTeamMembershipSnapshot team, List<Guid> eligibleUserIds,
        CancellationToken cancellationToken, Guid? singleUserSync = null, SyncStepResult? step = null)
    {
        var currentMemberIds = team.ActiveMemberUserIds.ToHashSet();

        var eligibleSet = eligibleUserIds.ToHashSet();

        // Single-user mode: only evaluate that user (don't remove others).
        var scopeIds = singleUserSync.HasValue
            ? [singleUserSync.Value]
            : currentMemberIds.Union(eligibleSet).ToHashSet();

        var toAdd = scopeIds.Where(id => eligibleSet.Contains(id) && !currentMemberIds.Contains(id)).ToList();
        var toRemove = scopeIds.Where(id => currentMemberIds.Contains(id) && !eligibleSet.Contains(id)).ToList();

        if (toAdd.Count == 0 && toRemove.Count == 0)
            return;

        var now = clock.GetCurrentInstant();

        var affectedUserIds = toAdd.Concat(toRemove).ToList();
        var usersById = await userService.GetUserInfosAsync(affectedUserIds, cancellationToken);
        var userNames = usersById.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.BurnerName);

        await teamService.ApplySystemTeamMembershipDeltaAsync(
            team.Id, toAdd, toRemove, now, cancellationToken);

        // Google sync stays outside the Teams section so its failures don't roll back the DB write.
        var addedAudits = new List<(Guid UserId, string UserName)>();
        foreach (var userId in toAdd)
        {
            var userName = userNames.GetValueOrDefault(userId, userId.ToString());
            step?.Added(userId, userName);
            addedAudits.Add((userId, userName));

            await googleSyncService.AddUserToTeamResourcesAsync(team.Id, userId, cancellationToken);
        }

        var removedAudits = new List<(Guid UserId, string UserName)>();
        foreach (var userId in toRemove)
        {
            var userName = userNames.GetValueOrDefault(userId, userId.ToString());
            step?.Removed(userId, userName);
            removedAudits.Add((userId, userName));

            await googleSyncService.RemoveUserFromTeamResourcesAsync(team.Id, userId, cancellationToken);
        }

        foreach (var (auditUserId, userName) in addedAudits)
        {
            await auditLogService.LogAsync(
                AuditAction.TeamMemberAdded, nameof(Team), team.Id,
                $"{userName} added to {team.Name} by system sync",
                nameof(SystemTeamSyncJob),
                relatedEntityId: auditUserId, relatedEntityType: nameof(User));
        }

        foreach (var (auditUserId, userName) in removedAudits)
        {
            await auditLogService.LogAsync(
                AuditAction.TeamMemberRemoved, nameof(Team), team.Id,
                $"{userName} removed from {team.Name} by system sync",
                nameof(SystemTeamSyncJob),
                relatedEntityId: auditUserId, relatedEntityType: nameof(User));
        }

        // Refresh sidebar claims for Volunteers churn before the 60s TTL.
        InvalidateUserCachesForSystemTeamMembershipChanges(team.SystemTeamType, affectedUserIds);

        logger.LogInformation(
            "Synced {TeamName} team: added {AddCount}, removed {RemoveCount}",
            team.Name, toAdd.Count, toRemove.Count);

        // Send "added to team" emails for new members; skip hidden teams.
        if (toAdd.Count > 0 && !team.IsHidden)
        {
            var resources = await TeamResourceService.GetTeamResourcesAsync(team.Id, cancellationToken);
            var resourceTuples = resources.Select(r => (r.Name, r.Url)).ToList();

            var addedUsersWithEmails = await userService
                .GetUserInfosAsync(toAdd, cancellationToken);

            foreach (var userId in toAdd)
            {
                if (!addedUsersWithEmails.TryGetValue(userId, out var user))
                    continue;

                try
                {
                    var email = user.Email!;
                    await emailService.SendAddedToTeamAsync(
                        email, user.BurnerName, team.Name, team.Slug,
                        resourceTuples, user.PreferredLanguage, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to send added-to-team email for user {UserId} team {TeamId}",
                        user.Id, team.Id);
                }
            }
        }
    }

    private void InvalidateUserCachesForSystemTeamMembershipChanges(
        SystemTeamType systemTeamType,
        IEnumerable<Guid> userIds)
    {
        if (systemTeamType != SystemTeamType.Volunteers)
        {
            return;
        }

        foreach (var userId in userIds)
        {
            roleAssignmentClaimsInvalidator.Invalidate(userId);
        }
    }
}
