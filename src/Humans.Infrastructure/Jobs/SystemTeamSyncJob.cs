using System.Diagnostics.Metrics;
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
using Humans.Application.Interfaces.Metering;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Metering;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Jobs;

/// <summary>
/// Background job that syncs membership for system-managed teams.
/// </summary>
/// <remarks>
/// All reads/writes fan out through section services
/// (<see cref="ITeamService"/>, <see cref="IUserService"/>,
/// <see cref="IProfileService"/>, <see cref="IApplicationDecisionService"/>,
/// <see cref="IRoleAssignmentService"/>, <see cref="ITeamResourceService"/>,
/// <see cref="ICampRepository"/>) so the job never touches
/// <see cref="Humans.Infrastructure.Data.HumansDbContext"/> directly
/// (design-rules §2c). Cross-cutting cache invalidation that crosses a
/// section boundary (claims principal refresh) routes through
/// <see cref="IRoleAssignmentClaimsCacheInvalidator"/> rather than
/// IMemoryCache; the ActiveTeams cache is owned and invalidated by
/// <see cref="ITeamService"/> itself on every mutating write.
/// </remarks>
public class SystemTeamSyncJob : ISystemTeamSync
{
    private readonly ITeamService _teamService;
    private readonly IUserService _userService;
    private readonly ICampRepository _campRepository;
    // IApplicationDecisionService, IRoleAssignmentService, IProfileService,
    // ITeamResourceService, and IMembershipCalculator are resolved lazily via
    // IServiceProvider to break DI cycles: ApplicationDecisionService and
    // RoleAssignmentService inject ISystemTeamSync directly, ProfileService
    // injects both, TeamResourceService injects IRoleAssignmentService, and
    // MembershipCalculator (via IMembershipQuery) depends on them. Direct
    // ctor injection here would form an unresolvable cycle. All calls below
    // happen after DI has finished building the graph, so deferring the
    // lookup is safe — same pattern MembershipCalculator uses for IConsentService.
    private readonly IServiceProvider _serviceProvider;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly IAuditLogService _auditLogService;
    private readonly IEmailService _emailService;
    private readonly IRoleAssignmentClaimsCacheInvalidator _roleAssignmentClaimsInvalidator;
    private readonly Counter<long> _jobRunsCounter;
    private readonly ILogger<SystemTeamSyncJob> _logger;
    private readonly IClock _clock;

    public SystemTeamSyncJob(
        ITeamService teamService,
        IUserService userService,
        ICampRepository campRepository,
        IServiceProvider serviceProvider,
        IGoogleSyncService googleSyncService,
        IAuditLogService auditLogService,
        IEmailService emailService,
        IRoleAssignmentClaimsCacheInvalidator roleAssignmentClaimsInvalidator,
        IMeters meters,
        ILogger<SystemTeamSyncJob> logger,
        IClock clock)
    {
        _teamService = teamService;
        _userService = userService;
        _campRepository = campRepository;
        _serviceProvider = serviceProvider;
        _googleSyncService = googleSyncService;
        _auditLogService = auditLogService;
        _emailService = emailService;
        _roleAssignmentClaimsInvalidator = roleAssignmentClaimsInvalidator;
        _jobRunsCounter = meters.RegisterCounter(
            "humans.job_runs_total",
            new MeterMetadata("Total background job runs", "{runs}"));
        _logger = logger;
        _clock = clock;
    }

    private IApplicationDecisionService ApplicationDecisionService =>
        _serviceProvider.GetRequiredService<IApplicationDecisionService>();

    private IRoleAssignmentService RoleAssignmentService =>
        _serviceProvider.GetRequiredService<IRoleAssignmentService>();

    private IProfileService ProfileService =>
        _serviceProvider.GetRequiredService<IProfileService>();

    private ITeamResourceService TeamResourceService =>
        _serviceProvider.GetRequiredService<ITeamResourceService>();

    private IMembershipCalculator MembershipCalculator =>
        _serviceProvider.GetRequiredService<IMembershipCalculator>();

    /// <summary>
    /// Executes the system team sync job and returns a report of what changed.
    /// </summary>
    public async Task<SyncReport> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting system team sync at {Time}", _clock.GetCurrentInstant());
        var report = new SyncReport();

        try
        {
            // These run sequentially to match the pre-migration behavior where
            // every step shared a single DbContext. Now each step calls
            // section services that own their own unit-of-work — the
            // sequential ordering still matters for downstream audit/notification
            // semantics (e.g. coordinator reconciliation must land before the
            // Coordinators-team sync).
            await SyncVolunteersTeamAsync(report, cancellationToken);
            await ReconcileCoordinatorRolesAsync(report, cancellationToken);
            await SyncCoordinatorsTeamAsync(report, cancellationToken);
            await SyncBoardTeamAsync(report, cancellationToken);
            await SyncAsociadosTeamAsync(report, cancellationToken);
            await SyncColaboradorsTeamAsync(report, cancellationToken);
            await SyncBarrioLeadsTeamAsync(report, cancellationToken);
            await BackfillGoogleEmailsAsync(report, cancellationToken);

            _jobRunsCounter.Add(1,
                new KeyValuePair<string, object?>("job", "system_team_sync"),
                new KeyValuePair<string, object?>("result", "success"));
            _logger.LogInformation("Completed system team sync");
        }
        catch (Exception ex)
        {
            _jobRunsCounter.Add(1,
                new KeyValuePair<string, object?>("job", "system_team_sync"),
                new KeyValuePair<string, object?>("result", "failure"));
            _logger.LogError(ex, "Error during system team sync");
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
        _logger.LogDebug("Reconciling coordinator roles with IsManagement assignments");
        var step = new SyncStepResult("Coordinator Role Reconciliation");

        // Load active memberships with role assignments + role definitions +
        // team metadata so we can decide promote / demote in memory without
        // touching DbContext.
        var memberships = await _teamService
            .GetActiveMembershipsForRoleReconciliationAsync(cancellationToken);

        var shouldBeCoordinator = memberships
            .Where(tm =>
                tm.Role == TeamMemberRole.Member &&
                tm.RoleAssignments.Any(ra => ra.TeamRoleDefinition.IsManagement))
            .ToList();

        var shouldBeMember = memberships
            .Where(tm =>
                tm.Role == TeamMemberRole.Coordinator &&
                tm.Team.SystemTeamType == SystemTeamType.None &&
                !tm.RoleAssignments.Any(ra => ra.TeamRoleDefinition.IsManagement))
            .ToList();

        if (shouldBeCoordinator.Count == 0 && shouldBeMember.Count == 0)
        {
            report?.Steps.Add(step);
            return;
        }

        // Stitch user display names for step-result audit output.
        var affectedUserIds = shouldBeCoordinator.Select(tm => tm.UserId)
            .Concat(shouldBeMember.Select(tm => tm.UserId))
            .Distinct()
            .ToList();
        var userNamesById = await _userService.GetByIdsAsync(affectedUserIds, cancellationToken);

        var changes = new List<(Guid TeamMemberId, TeamMemberRole Role)>(
            shouldBeCoordinator.Count + shouldBeMember.Count);

        foreach (var member in shouldBeCoordinator)
        {
            changes.Add((member.Id, TeamMemberRole.Coordinator));
            var userName = userNamesById.TryGetValue(member.UserId, out var u)
                ? u.DisplayName : member.UserId.ToString();
            var teamName = member.Team.Name;
            step.Fixed(member.UserId, userName, $"Promoted to Coordinator on {teamName}");
            _logger.LogInformation(
                "Reconciled {UserName} to Coordinator on team {TeamId} (had IsManagement role assignment)",
                userName, member.TeamId);
        }

        foreach (var member in shouldBeMember)
        {
            changes.Add((member.Id, TeamMemberRole.Member));
            var userName = userNamesById.TryGetValue(member.UserId, out var u)
                ? u.DisplayName : member.UserId.ToString();
            var teamName = member.Team.Name;
            step.Fixed(member.UserId, userName, $"Demoted to Member on {teamName} (no IsManagement role)");
            _logger.LogInformation(
                "Reconciled {UserName} to Member on team {TeamId} (no IsManagement role assignment)",
                userName, member.TeamId);
        }

        // Apply all role changes in a single save through the Teams section.
        // The service invalidates the ActiveTeams cache when at least one
        // change lands, so no direct cache call here.
        await _teamService.ApplyMemberRoleChangesAsync(changes, cancellationToken);

        report?.Steps.Add(step);
    }

    /// <summary>
    /// Syncs the Volunteers team membership based on document compliance.
    /// Members: All users with all required documents signed.
    /// </summary>
    public async Task SyncVolunteersTeamAsync(SyncReport? report = null, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Syncing Volunteers team");
        var step = new SyncStepResult("Volunteers");

        var team = await _teamService.GetSystemTeamWithActiveMembersAsync(
            SystemTeamType.Volunteers, cancellationToken);
        if (team is null)
        {
            _logger.LogWarning("Volunteers system team not found");
            report?.Steps.Add(step);
            return;
        }

        // All users with profiles that are approved and not suspended.
        var allApprovedIds = await ProfileService.GetActiveApprovedUserIdsAsync(cancellationToken);

        // Use shared partition to determine eligibility (Active = approved +
        // not suspended + all consents signed).
        var partition = await MembershipCalculator.PartitionUsersAsync(allApprovedIds, cancellationToken);
        var eligibleUserIds = partition.Active.ToList();

        await SyncTeamMembershipAsync(team, eligibleUserIds, cancellationToken, step: step);
        report?.Steps.Add(step);
    }

    /// <summary>
    /// Syncs the Coordinators team membership based on Coordinator roles.
    /// Members: All users who are Coordinator of any team.
    /// </summary>
    public async Task SyncCoordinatorsTeamAsync(SyncReport? report = null, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Syncing Coordinators team");
        var step = new SyncStepResult("Coordinators");

        var team = await _teamService.GetSystemTeamWithActiveMembersAsync(
            SystemTeamType.Coordinators, cancellationToken);
        if (team is null)
        {
            _logger.LogWarning("Coordinators system team not found");
            report?.Steps.Add(step);
            return;
        }

        // Department-level coordinators only (sub-team managers excluded).
        var leadUserIds = await _teamService.GetActiveDepartmentCoordinatorUserIdsAsync(cancellationToken);

        // Additionally filter by Coordinators-team-required consents.
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
        _logger.LogDebug("Syncing Board team");
        var step = new SyncStepResult("Board");

        var team = await _teamService.GetSystemTeamWithActiveMembersAsync(
            SystemTeamType.Board, cancellationToken);
        if (team is null)
        {
            _logger.LogWarning("Board system team not found");
            report?.Steps.Add(step);
            return;
        }

        // Users with active Board role assignment (service reads the clock).
        var boardMemberIds = await RoleAssignmentService.GetActiveUserIdsInRoleAsync(
            RoleNames.Board, cancellationToken);

        // Additionally filter by Board-team-required consents.
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
        _logger.LogDebug("Syncing {TeamType} team", teamType);
        var step = new SyncStepResult(teamType.ToString());

        var team = await _teamService.GetSystemTeamWithActiveMembersAsync(teamType, cancellationToken);
        if (team is null)
        {
            _logger.LogWarning("{TeamType} system team not found", teamType);
            report?.Steps.Add(step);
            return;
        }

        var today = _clock.GetCurrentInstant().InUtc().Date;

        var applicationUserIds = await ApplicationDecisionService
            .GetActiveApprovedTierUserIdsAsync(tier, today, cancellationToken);

        // Filter by profile status to match per-user sync behavior.
        var allApprovedIds = await ProfileService.GetActiveApprovedUserIdsAsync(cancellationToken);
        var approvedSet = allApprovedIds.ToHashSet();
        var userIds = applicationUserIds.Where(approvedSet.Contains).ToList();

        var eligibleSet = await MembershipCalculator.GetUsersWithAllRequiredConsentsForTeamAsync(
            userIds, teamId, cancellationToken);
        var eligibleUserIds = eligibleSet.ToList();

        // Downgrade Profile.MembershipTier for users who no longer have an
        // active approved application for this tier. Before downgrading to
        // Volunteer, check if the user holds an active application for the
        // OTHER higher tier.
        var todayInstant = _clock.GetCurrentInstant();
        var otherTierByUser = await ApplicationDecisionService
            .GetOtherActiveTierAssignmentsAsync(tier, today, cancellationToken);

        var downgrades = await ProfileService.DowngradeTierForExpiredAsync(
            tier, applicationUserIds, otherTierByUser, todayInstant, cancellationToken);

        if (downgrades.Count > 0)
        {
            var downgradeUserIds = downgrades.Select(d => d.UserId).ToList();
            var downgradeUsersById = await _userService.GetByIdsAsync(downgradeUserIds, cancellationToken);

            foreach (var (downgradeUserId, newTier) in downgrades)
            {
                var displayName = downgradeUsersById.TryGetValue(downgradeUserId, out var u)
                    ? u.DisplayName : "Unknown";
                await _auditLogService.LogAsync(
                    AuditAction.TierDowngraded, nameof(Profile), downgradeUserId,
                    $"Membership tier changed to {newTier} for {displayName} due to {tier} term expiry",
                    nameof(SystemTeamSyncJob),
                    relatedEntityId: downgradeUserId, relatedEntityType: nameof(User));
            }
        }

        await SyncTeamMembershipAsync(team, eligibleUserIds, cancellationToken, step: step);
        report?.Steps.Add(step);
    }

    /// <summary>
    /// Syncs Volunteers team membership for a single user. Call this after approving
    /// a volunteer or after they complete their required consents.
    /// </summary>
    public async Task SyncVolunteersMembershipForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var team = await _teamService.GetSystemTeamWithActiveMembersAsync(
            SystemTeamType.Volunteers, cancellationToken);
        if (team is null)
        {
            _logger.LogWarning("Volunteers system team not found");
            return;
        }

        var profiles = await ProfileService.GetByUserIdsAsync([userId], cancellationToken);
        profiles.TryGetValue(userId, out var profile);

        var isEligible = profile is { IsApproved: true, IsSuspended: false }
            && await MembershipCalculator.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, cancellationToken);

        // Build a single-user eligible list and let the existing sync logic handle add/remove
        var eligibleUserIds = isEligible ? [userId] : new List<Guid>();
        await SyncTeamMembershipAsync(team, eligibleUserIds, cancellationToken, singleUserSync: userId);
    }

    /// <summary>
    /// Syncs Coordinators team membership for a single user. Call this after changing
    /// a team member's role to/from Coordinator.
    /// </summary>
    public async Task SyncCoordinatorsMembershipForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var team = await _teamService.GetSystemTeamWithActiveMembersAsync(
            SystemTeamType.Coordinators, cancellationToken);
        if (team is null)
        {
            _logger.LogWarning("Coordinators system team not found");
            return;
        }

        var isCoordinatorAnywhere = await _teamService.IsActiveDepartmentCoordinatorAsync(userId, cancellationToken);

        var isEligible = isCoordinatorAnywhere
            && await MembershipCalculator.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Coordinators, cancellationToken);

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
        var team = await _teamService.GetSystemTeamWithActiveMembersAsync(teamType, cancellationToken);
        if (team is null)
        {
            _logger.LogWarning("{TeamType} system team not found", teamType);
            return;
        }

        var today = _clock.GetCurrentInstant().InUtc().Date;

        var hasApprovedApp = await ApplicationDecisionService
            .HasActiveApprovedTierAsync(userId, tier, today, cancellationToken);

        var profiles = await ProfileService.GetByUserIdsAsync([userId], cancellationToken);
        profiles.TryGetValue(userId, out var profile);

        var isEligible = hasApprovedApp
            && profile is { IsApproved: true, IsSuspended: false }
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
        _logger.LogDebug("Syncing Barrio Leads team");
        var step = new SyncStepResult("Barrio Leads");

        var team = await _teamService.GetSystemTeamWithActiveMembersAsync(
            SystemTeamType.BarrioLeads, cancellationToken);
        if (team is null)
        {
            _logger.LogWarning("Barrio Leads system team not found");
            report?.Steps.Add(step);
            return;
        }

        var activeLeadUserIds = await _campRepository.GetActiveLeadUserIdsAsync(cancellationToken);
        var eligibleUserIds = activeLeadUserIds.ToList();

        await SyncTeamMembershipAsync(team, eligibleUserIds, cancellationToken, step: step);
        report?.Steps.Add(step);
    }

    /// <summary>
    /// Syncs Barrio Leads team membership for a single user. Call this after adding
    /// or removing a camp lead assignment.
    /// </summary>
    public async Task SyncBarrioLeadsMembershipForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var team = await _teamService.GetSystemTeamWithActiveMembersAsync(
            SystemTeamType.BarrioLeads, cancellationToken);
        if (team is null)
        {
            _logger.LogWarning("Barrio Leads system team not found");
            return;
        }

        var isLeadAnywhere = await _campRepository.IsLeadAnywhereAsync(userId, cancellationToken);

        // Idempotency guard: if the user should be a member and already has an
        // active team_members row, do nothing. This avoids unique-index
        // violations (IX_team_members_active_unique) on the Barrio Leads team
        // when the user is registering another camp and already has an active
        // membership from a previous registration.
        if (isLeadAnywhere)
        {
            var alreadyActive = team.Members.Any(m => m.UserId == userId && m.LeftAt == null);
            if (alreadyActive)
            {
                return;
            }
        }

        var eligibleUserIds = isLeadAnywhere ? [userId] : new List<Guid>();
        await SyncTeamMembershipAsync(team, eligibleUserIds, cancellationToken, singleUserSync: userId);
    }

    /// <summary>
    /// Backfills User.GoogleEmail for users who have a verified @nobodies.team email
    /// but a null GoogleEmail. This ensures Google Group sync uses the correct address.
    /// </summary>
    private async Task BackfillGoogleEmailsAsync(SyncReport? report = null, CancellationToken cancellationToken = default)
    {
        var step = new SyncStepResult("Google Email Backfill");

        var backfilled = await _userService.BackfillNobodiesTeamGoogleEmailsAsync(cancellationToken);
        foreach (var (userId, displayName, googleEmail) in backfilled)
        {
            step.Fixed(userId, displayName, $"Set GoogleEmail to {googleEmail}");
            _logger.LogInformation(
                "Backfilled GoogleEmail for {User} to {Email}", displayName, googleEmail);
        }

        report?.Steps.Add(step);
    }

    private async Task SyncTeamMembershipAsync(Team team, List<Guid> eligibleUserIds,
        CancellationToken cancellationToken, Guid? singleUserSync = null, SyncStepResult? step = null)
    {
        var currentMemberIds = team.Members
            .Where(m => m.LeftAt is null)
            .Select(m => m.UserId)
            .ToHashSet();

        var eligibleSet = eligibleUserIds.ToHashSet();

        // When syncing a single user, only evaluate that user (don't remove others).
        var scopeIds = singleUserSync.HasValue
            ? new HashSet<Guid> { singleUserSync.Value }
            : currentMemberIds.Union(eligibleSet).ToHashSet();

        // Users to add (in eligible but not current members).
        var toAdd = scopeIds.Where(id => eligibleSet.Contains(id) && !currentMemberIds.Contains(id)).ToList();

        // Users to remove (current members but not in eligible).
        var toRemove = scopeIds.Where(id => currentMemberIds.Contains(id) && !eligibleSet.Contains(id)).ToList();

        if (toAdd.Count == 0 && toRemove.Count == 0)
            return;

        var now = _clock.GetCurrentInstant();

        // Batch-load display names for affected users via IUserService.
        var affectedUserIds = toAdd.Concat(toRemove).ToList();
        var usersById = await _userService.GetByIdsAsync(affectedUserIds, cancellationToken);
        var userNames = usersById.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.DisplayName);

        // Apply the bulk membership delta in a single save through the Teams
        // section (also cascades TeamRoleAssignment deletes on soft-remove and
        // invalidates the ActiveTeams cache).
        await _teamService.ApplySystemTeamMembershipDeltaAsync(
            team.Id, toAdd, toRemove, now, cancellationToken);

        // Fan out Google sync adds per user — the call stays outside the
        // Teams section so sync-service failures don't tear down the DB
        // write (which is the behavior the pre-migration job had).
        var addedAudits = new List<(Guid UserId, string UserName)>();
        foreach (var userId in toAdd)
        {
            var userName = userNames.GetValueOrDefault(userId, userId.ToString());
            step?.Added(userId, userName);
            addedAudits.Add((userId, userName));

            await _googleSyncService.AddUserToTeamResourcesAsync(team.Id, userId, cancellationToken);
        }

        // Fan out Google sync removes per user.
        var removedAudits = new List<(Guid UserId, string UserName)>();
        foreach (var userId in toRemove)
        {
            var userName = userNames.GetValueOrDefault(userId, userId.ToString());
            step?.Removed(userId, userName);
            removedAudits.Add((userId, userName));

            await _googleSyncService.RemoveUserFromTeamResourcesAsync(team.Id, userId, cancellationToken);
        }

        foreach (var (auditUserId, userName) in addedAudits)
        {
            await _auditLogService.LogAsync(
                AuditAction.TeamMemberAdded, nameof(Team), team.Id,
                $"{userName} added to {team.Name} by system sync",
                nameof(SystemTeamSyncJob),
                relatedEntityId: auditUserId, relatedEntityType: nameof(User));
        }

        foreach (var (auditUserId, userName) in removedAudits)
        {
            await _auditLogService.LogAsync(
                AuditAction.TeamMemberRemoved, nameof(Team), team.Id,
                $"{userName} removed from {team.Name} by system sync",
                nameof(SystemTeamSyncJob),
                relatedEntityId: auditUserId, relatedEntityType: nameof(User));
        }

        // Invalidate per-user role-assignment-claim caches for Volunteers
        // changes so the sidebar claims transform refreshes before the 60s
        // TTL elapses (matches pre-migration behavior).
        InvalidateUserCachesForSystemTeamMembershipChanges(team.SystemTeamType, affectedUserIds);

        _logger.LogInformation(
            "Synced {TeamName} team: added {AddCount}, removed {RemoveCount}",
            team.Name, toAdd.Count, toRemove.Count);

        // Send "added to team" emails for newly added members (skip hidden teams).
        if (toAdd.Count > 0 && !team.IsHidden)
        {
            var resources = await TeamResourceService.GetTeamResourcesAsync(team.Id, cancellationToken);
            var resourceTuples = resources.Select(r => (r.Name, r.Url)).ToList();

            var addedUsersWithEmails = await _userService
                .GetByIdsWithEmailsAsync(toAdd, cancellationToken);

            foreach (var userId in toAdd)
            {
                if (!addedUsersWithEmails.TryGetValue(userId, out var user))
                    continue;

                try
                {
#pragma warning disable CS0618 // User.GetEffectiveEmail() cross-domain nav — covered by §15i follow-up.
                    var email = user.GetEffectiveEmail() ?? user.Email!;
#pragma warning restore CS0618
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
            _roleAssignmentClaimsInvalidator.Invalidate(userId);
        }
    }
}
