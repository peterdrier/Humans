using System.Globalization;
using Humans.GoogleIntegration.Contracts;
using Humans.Auth.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Base.Extensions;
using Humans.Base.Helpers;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces.Caching;
using Humans.EarlyEntry.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Notifications.Contracts;
using Humans.Teams.Data;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Teams.Domain;
using Humans.Base.Enums;
using Humans.Users.Contracts;

namespace Humans.Teams.Services;

internal sealed class TeamService(
    ITeamRepository repo,
    IAuditLogService auditLogService,
    INotificationEmitter notificationService,
    IShiftService shiftManagementService,
    IAdminAuthorizationService adminAuthorization,
    IServiceProvider serviceProvider,
    IClock clock,
    ILogger<TeamService> logger) : ITeamManagementService, ITeamSeeding, IGoogleGroupMembershipSource, IUserMerge, IEarlyEntryProvider
{
    private readonly TeamDataExport dataExport = new(repo, serviceProvider);
    private readonly TeamPageContent pageContentOwner = new(repo, clock, auditLogService, logger);
    private readonly TeamSnapshotReader snapshotReader = new(repo, serviceProvider, shiftManagementService);
    private TeamJoining Joining => new(repo, auditLogService, clock, logger, snapshotReader, membershipOutbox, membershipNotices);
    private TeamRoleDefinitions RoleDefinitions => new(repo, auditLogService, clock, serviceProvider, snapshotReader);
    private TeamEarlyEntry EarlyEntry => new(repo, auditLogService, clock, snapshotReader);
    private readonly TeamMembershipOutbox membershipOutbox = new(repo, serviceProvider, clock, logger);
    private readonly TeamMembershipNotices membershipNotices = new(notificationService, serviceProvider, logger);
    private readonly TeamGoogleGroupMemberships googleGroupMemberships = new(repo, logger);
    private const int MaxTeamSlugLength = 256;
    private static readonly HashSet<string> ReservedTeamSlugs = new(StringComparer.Ordinal)
    {
        "roster", "birthdays", "map", "my", "sync", "summary", "create", "search", "tree"
    };

    internal const string TeamMemberships = TeamDataExport.TeamMemberships;
    internal const string TeamJoinRequests = TeamDataExport.TeamJoinRequests;
    internal const string TeamEarlyEntry = TeamDataExport.TeamEarlyEntry;

    // Lazy: UserService injects ITeamManagement, closing a cycle.
    private ITeamResourceService TeamResourceService
        => serviceProvider.GetRequiredService<ITeamResourceService>();

    private IRoleAssignmentService RoleAssignmentService
        => serviceProvider.GetRequiredService<IRoleAssignmentService>();

    private ISystemTeamSync SystemTeamSync
        => serviceProvider.GetRequiredService<ISystemTeamSync>();

    private IGoogleSyncOutboxService GoogleSyncOutboxService
        => serviceProvider.GetRequiredService<IGoogleSyncOutboxService>();

    private IGoogleSyncWrite GoogleSyncService
        => serviceProvider.GetRequiredService<IGoogleSyncWrite>();

    public async Task<Team> CreateTeamAsync(
        string name,
        string? description,
        bool requiresApproval,
        Guid? parentTeamId = null,
        string? googleGroupPrefix = null,
        bool isHidden = false,
        CancellationToken cancellationToken = default)
    {
        var result = await CreateTeamCoreAsync(name, description, requiresApproval, parentTeamId,
            googleGroupPrefix, isHidden, cancellationToken);
        result.RequireSuccess();
        return result.Team!;
    }

    private async Task<TeamUpdateResult> CreateTeamCoreAsync(
        string name,
        string? description,
        bool requiresApproval,
        Guid? parentTeamId = null,
        string? googleGroupPrefix = null,
        bool isHidden = false,
        CancellationToken cancellationToken = default)
    {
        var baseSlug = SlugHelper.GenerateSlug(name);
        if (baseSlug.Length == 0)
            baseSlug = "team";
        var now = clock.GetCurrentInstant();

        if (ReservedTeamSlugs.Contains(baseSlug))
            return new(ErrorMessage: $"The team name '{name}' conflicts with a reserved route");

        if (parentTeamId.HasValue)
        {
            if (await ValidateParentAsync(parentTeamId.Value, cancellationToken) is { } parentError)
                return new(ErrorMessage: parentError);
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var slug = baseSlug;
            if (attempt > 0)
            {
                var suffix = "-" + (attempt + 1).ToString(CultureInfo.InvariantCulture);
                var prefixLength = Math.Min(baseSlug.Length, MaxTeamSlugLength - suffix.Length);
                slug = baseSlug[..prefixLength].TrimEnd('-') + suffix;
            }

            var collidesWithExistingSlug = await repo.SlugExistsAsync(slug, excludingTeamId: null, cancellationToken);
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

            var persisted = await repo.AddTeamWithRequiresApprovalOverrideAsync(team, requiresApproval, cancellationToken);
            if (persisted == TeamUpdateConflict.None)
            {
                logger.LogInformation("Created team {TeamName} with slug {Slug}", name, slug);
                return new(Team: team);
            }

            if (persisted is not (TeamUpdateConflict.SlugTaken or TeamUpdateConflict.CustomSlugTaken))
                return new(Conflict: persisted);

            // Slug unique-constraint race: retry with the next suffix.
            logger.LogDebug(
                "Slug collision for '{Slug}' at persist, retrying (attempt {Attempt})",
                slug, attempt + 1);
        }

        return new(ErrorMessage: $"Could not generate unique slug for team '{name}' after 10 attempts");
    }

    public async Task<TeamInfo?> GetTeamBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        var teamsById = await LoadTeamsByIdAsync(cancellationToken);
        return TeamSlugResolver.Find(teamsById, slug);
    }

    public Task<TeamInfo?> GetTeamAsync(Guid teamId, CancellationToken cancellationToken = default) =>
        snapshotReader.GetTeamAsync(teamId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamsAsync(
        CancellationToken cancellationToken = default) =>
        await LoadTeamsByIdAsync(cancellationToken);

    // Served from CachingTeamService's snapshot: reaching the inner service means a DI mistake.
    public Task<IReadOnlyList<TeamSearchHit>> SearchAsync(
        string query, int max,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Team search runs against the cached TeamInfo snapshot in CachingTeamService. " +
            "If this is being called on the inner TeamService it indicates a DI registration " +
            "mistake — ITeamService must resolve to the caching decorator.");

    public async Task<IReadOnlyCollection<Guid>> GetEffectiveBudgetCoordinatorTeamIdsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var departmentIds = await repo.GetUserDepartmentCoordinatorTeamIdsAsync(userId, cancellationToken);
        var teamIds = departmentIds.ToHashSet();

        if (teamIds.Count == 0)
            return teamIds;

        var childTeams = await repo.GetActiveChildIdsByParentsAsync(teamIds.ToList(), cancellationToken);
        foreach (var (childId, _) in childTeams)
            teamIds.Add(childId);

        return teamIds;
    }

    public async Task<(bool Updated, string? PreviousPrefix)> SetGoogleGroupPrefixAsync(
        Guid teamId, string? prefix, CancellationToken cancellationToken = default)
    {
        var (updated, previous) = await repo.SetGoogleGroupPrefixAsync(teamId, prefix, cancellationToken);
        return (updated, previous);
    }

    public async Task<IReadOnlyList<UserTeamMembershipInfo>> GetUserTeamMembershipsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var memberships = await repo.GetActiveByUserIdAsync(userId, cancellationToken);
        return memberships
            .Select(m => new UserTeamMembershipInfo(
                m.Id, m.TeamId, m.Team.Name, m.Team.Slug, m.Team.IsSystemTeam, m.Role, m.JoinedAt))
            .ToList();
    }

    private async Task<string?> ValidateParentAsync(Guid parentTeamId, CancellationToken ct)
    {
        var parent = await repo.GetByIdAsync(parentTeamId, ct);
        if (parent is null) return $"Parent team {parentTeamId} not found";
        if (parent.IsSystemTeam) return "System teams cannot be parents";
        return parent.ParentTeamId.HasValue
            ? "Cannot nest more than one level — the parent team already has a parent"
            : null;
    }

    public async Task<TeamUpdateResult> UpdateTeamAsync(
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
        bool? earlyEntryEnabled = null,
        CancellationToken cancellationToken = default)
    {
        var team = await repo.FindForMutationAsync(teamId, cancellationToken);
        if (team is null) return new(ErrorMessage: $"Team {teamId} not found");

        if (team.IsSystemTeam)
        {
            team.Description = description;
            team.GoogleGroupPrefix = googleGroupPrefix;
            team.UpdatedAt = clock.GetCurrentInstant();
            var conflict = await repo.UpdateTeamAsync(team, cancellationToken);
            return conflict == TeamUpdateConflict.None ? new(Team: team) : new(Conflict: conflict);
        }

        if (parentTeamId.HasValue)
        {
            if (parentTeamId.Value == teamId)
                return new(ErrorMessage: "A team cannot be its own parent");

            var hasChildren = await repo.HasActiveChildrenAsync(teamId, cancellationToken);
            if (hasChildren)
                return new(ErrorMessage: "This team has sub-teams and cannot become a child of another team");

            if (await ValidateParentAsync(parentTeamId.Value, cancellationToken) is { } parentError)
                return new(ErrorMessage: parentError);
        }

        if (!string.IsNullOrWhiteSpace(customSlug))
        {
            var normalized = SlugHelper.GenerateSlug(customSlug);
            if (string.IsNullOrEmpty(normalized))
                return new(ErrorMessage: "Custom slug is not valid. Use lowercase letters, numbers, and hyphens.");
            if (ReservedTeamSlugs.Contains(normalized))
                return new(ErrorMessage: $"The slug '{normalized}' conflicts with a reserved route.");

            var customSlugTaken = await repo.SlugExistsAsync(normalized, excludingTeamId: teamId, cancellationToken);
            if (customSlugTaken)
                return new(ErrorMessage: $"The slug '{normalized}' is already in use by another team.");

            customSlug = normalized;
        }
        else
        {
            customSlug = null;
        }

        var wasActiveDepartment = team.IsActive && !team.ParentTeamId.HasValue;
        var willBeActiveDepartment = isActive && !parentTeamId.HasValue;
        var coordinatorUserIds = wasActiveDepartment != willBeActiveDepartment
            ? await repo.GetActiveCoordinatorUserIdsForTeamAsync(teamId, cancellationToken)
            : (IReadOnlyList<Guid>)[];

        var nameChanged = !string.Equals(team.Name, name, StringComparison.Ordinal);
        if (nameChanged)
        {
            var newSlug = SlugHelper.GenerateSlug(name);
            if (!string.IsNullOrEmpty(newSlug) && !ReservedTeamSlugs.Contains(newSlug)
                && !await repo.SlugExistsAsync(newSlug, excludingTeamId: teamId, cancellationToken))
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
        if (isSensitive.HasValue && isSensitive.Value != team.IsSensitive)
        {
            // Only a global Admin writes IsSensitive, enforced here so every caller is held to it. Gated on the change,
            // not the argument: a caller passing the current value (the seeders do) isn't writing.
            await adminAuthorization.RequireCurrentUserIsAdminAsync(cancellationToken);
            team.IsSensitive = isSensitive.Value;
        }
        if (isPromotedToDirectory.HasValue)
            team.IsPromotedToDirectory = isPromotedToDirectory.Value;
        if (earlyEntryEnabled is { } eeFlag)
            team.EarlyEntryEnabled = eeFlag;
        if (team.IsSystemTeam || parentTeamId.HasValue)
        {
            team.IsPublicPage = false;
            team.ShowCoordinatorsOnPublicPage = false;
        }
        team.UpdatedAt = clock.GetCurrentInstant();

        var updateConflict = await repo.UpdateTeamAsync(team, cancellationToken);
        if (updateConflict != TeamUpdateConflict.None) return new(Conflict: updateConflict);

        foreach (var userId in coordinatorUserIds)
            await SystemTeamSync.SyncMembershipForUserAsync(userId, SystemTeamType.Coordinators, cancellationToken);

        logger.LogInformation("Updated team {TeamId} ({TeamName})", teamId, name);

        return new(Team: team);
    }

    public Task<TeamPageUpdateResult> UpdateTeamPageContentAsync(
        Guid teamId,
        string? pageContent,
        IReadOnlyList<TeamPageCallToActionInput> callsToAction,
        bool isPublicPage,
        bool showCoordinatorsOnPublicPage,
        Guid updatedByUserId,
        CancellationToken cancellationToken = default) =>
        pageContentOwner.UpdateTeamPageContentAsync(teamId, pageContent, callsToAction,
            isPublicPage, showCoordinatorsOnPublicPage, updatedByUserId, cancellationToken);

    public async Task<TeamUpdateResult> CreateTeamWithGoogleGroupAsync(
        string name,
        string? description,
        bool requiresApproval,
        Guid? parentTeamId = null,
        string? googleGroupPrefix = null,
        bool isHidden = false,
        CancellationToken cancellationToken = default)
    {
        var result = await CreateTeamCoreAsync(
            name, description, requiresApproval, parentTeamId, googleGroupPrefix, isHidden, cancellationToken);
        if (!result.Succeeded) return result;
        var team = result.Team!;

        string? groupWarning = null;
        if (!string.IsNullOrEmpty(googleGroupPrefix))
        {
            // Provisioning reads Teams back through its cache before the outer mutation completes.
            serviceProvider.GetRequiredService<ITeamManagementService>().InvalidateActiveTeamsCache();
            try
            {
                var groupResult = await GoogleSyncService.EnsureTeamGroupAsync(team.Id, cancellationToken: cancellationToken);
                groupWarning = GroupLinkWarning(groupResult);
                if (groupWarning is not null)
                    logger.LogWarning("Google Group setup incomplete for new team {TeamId}: {Reason}", team.Id, groupWarning);
                if (groupResult.RequiresConfirmation)
                    return result with { GroupWarning = groupWarning };
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create Google Group for new team {TeamId}, clearing prefix", team.Id);
                groupWarning = ex.Message;
            }

            if (groupWarning is not null)
            {
                // Clear the prefix so the team never points at a missing group; the admin can retry from team edit.
                result = await UpdateTeamAsync(
                    team.Id, team.Name, team.Description, team.RequiresApproval, team.IsActive,
                    team.ParentTeamId, googleGroupPrefix: null, cancellationToken: cancellationToken);
                result.RequireSuccess();
                groupWarning = $"Team created but Google Group setup failed: {groupWarning}. The group prefix has been cleared.";
            }
        }

        return result with { GroupWarning = groupWarning };
    }

    public async Task<TeamUpdateResult> UpdateTeamWithGoogleGroupAsync(
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
        bool? earlyEntryEnabled = null,
        CancellationToken cancellationToken = default)
    {
        var result = await UpdateTeamAsync(
            teamId, name, description, requiresApproval, isActive, parentTeamId,
            googleGroupPrefix, customSlug, hasBudget, isHidden, isSensitive,
            isPromotedToDirectory, earlyEntryEnabled, cancellationToken);

        if (!result.Succeeded) return result;

        // The callback must see the saved prefix, including a cleared one, rather than the previous snapshot.
        serviceProvider.GetRequiredService<ITeamManagementService>().InvalidateActiveTeamsCache();
        string? groupWarning;
        try
        {
            var groupResult = await GoogleSyncService.EnsureTeamGroupAsync(teamId, cancellationToken: cancellationToken);
            groupWarning = GroupLinkWarning(groupResult);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to sync Google Group for team {TeamId}", teamId);
            groupWarning = $"Team updated but Google Group setup failed: {ex.Message}";
        }

        return result with { GroupWarning = groupWarning };
    }

    private static string? GroupLinkWarning(GroupLinkResult result) =>
        result.Success
            ? null
            : result.RequiresConfirmation
                ? result.WarningMessage ?? "Confirmation required for group reactivation."
                : result.ErrorMessage ?? "Google Group linking failed.";

    public async Task<TeamDeactivationResult> DeleteTeamAsync(Guid teamId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdAsync(teamId, cancellationToken);
        if (team is null)
            return new(Refusal: $"Team {teamId} not found");

        if (team.IsSystemTeam)
            return new(Refusal: "Cannot delete system team");

        var hasActiveChildren = await repo.HasActiveChildrenAsync(teamId, cancellationToken);
        if (hasActiveChildren)
            return new(Refusal: "Cannot deactivate a team that has active sub-teams. Remove or reassign sub-teams first.");

        var now = clock.GetCurrentInstant();

        var coordinatorUserIds = team.ParentTeamId.HasValue
            ? (IReadOnlyList<Guid>)[]
            : await repo.GetActiveCoordinatorUserIdsForTeamAsync(teamId, cancellationToken);
        var closedUsers = await repo.DeactivateTeamAsync(teamId, now, cancellationToken);
        foreach (var userId in closedUsers)
        {
            await auditLogService.LogAsync(
                TeamsAuditActions.TeamMemberRemoved, nameof(Team), teamId,
                $"Membership ended when {team.Name} was deactivated",
                actorUserId, relatedEntityId: userId, relatedEntityType: AuditSubjectTypes.User);
        }

        foreach (var userId in coordinatorUserIds)
            await SystemTeamSync.SyncMembershipForUserAsync(userId, SystemTeamType.Coordinators, cancellationToken);

        logger.LogInformation(
            "Deactivated team {TeamId} ({TeamName}); closed {MemberCount} memberships",
            teamId, team.Name, closedUsers.Count);
        return new();
    }

    public Task<TeamJoinResult> JoinTeamAsync(
        Guid teamId, Guid userId, string? message, CancellationToken cancellationToken = default) =>
        Joining.JoinTeamAsync(teamId, userId, message, cancellationToken);

    public Task<TeamJoinRequestResult> RequestToJoinTeamAsync(
        Guid teamId, Guid userId, string? message, CancellationToken cancellationToken = default) =>
        Joining.RequestToJoinTeamAsync(teamId, userId, message, cancellationToken);

    public Task<TeamDirectJoinResult> JoinTeamDirectlyAsync(
        Guid teamId, Guid userId, CancellationToken cancellationToken = default) =>
        Joining.JoinTeamDirectlyAsync(teamId, userId, cancellationToken);

    public async Task<TeamMembershipMutationResult> LeaveTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdAsync(teamId, cancellationToken);
        if (team is null)
            return new TeamMembershipMutationResult(false, "Teams_NotFound");

        if (team.IsSystemTeam)
            return new TeamMembershipMutationResult(false, "Teams_CannotLeaveSystem");

        var member = await repo.FindActiveMemberForMutationAsync(teamId, userId, cancellationToken);
        if (member is null)
            return new TeamMembershipMutationResult(false, "Teams_NotMember");

        var wasCoordinator = member.Role == TeamMemberRole.Coordinator;

        await membershipOutbox.MarkMemberLeftWithOutboxAsync(member, null, cancellationToken);

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamLeft, nameof(Team), teamId,
            $"Left {team.Name}",
            userId,
            relatedEntityId: userId, relatedEntityType: AuditSubjectTypes.User);

        if (wasCoordinator)
        {
            await SystemTeamSync.SyncMembershipForUserAsync(
                userId, SystemTeamType.Coordinators, cancellationToken);
        }

        return new TeamMembershipMutationResult(true, WasCoordinator: wasCoordinator);
    }

    public Task<TeamMembershipMutationResult> WithdrawJoinRequestAsync(
        Guid requestId, Guid userId, CancellationToken cancellationToken = default) =>
        Joining.WithdrawJoinRequestAsync(requestId, userId, cancellationToken);

    public Task<TeamJoinReviewResult> ApproveJoinRequestAsync(
        Guid requestId, Guid approverUserId, string? notes, CancellationToken cancellationToken = default) =>
        Joining.ApproveJoinRequestAsync(requestId, approverUserId, notes, cancellationToken);

    public Task<TeamJoinReviewResult> RejectJoinRequestAsync(
        Guid requestId, Guid approverUserId, string reason, CancellationToken cancellationToken = default) =>
        Joining.RejectJoinRequestAsync(requestId, approverUserId, reason, cancellationToken);

    public Task<IReadOnlyList<TeamJoinRequestSnapshot>> GetPendingRequestsForTeamAsync(
        Guid teamId, CancellationToken cancellationToken = default) =>
        snapshotReader.GetPendingRequestsForTeamAsync(teamId, cancellationToken);

    public Task<IReadOnlyList<TeamJoinRequestSnapshot>> GetPendingRequestsForUserAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        snapshotReader.GetPendingRequestsForUserAsync(userId, cancellationToken);

    public Task<TeamJoinRequestSnapshot?> GetUserPendingRequestAsync(
        Guid teamId, Guid userId, CancellationToken cancellationToken = default) =>
        snapshotReader.GetUserPendingRequestAsync(teamId, userId, cancellationToken);

    public Task<bool> IsUserCoordinatorOfTeamAsync(
        Guid teamId, Guid userId, CancellationToken cancellationToken = default) =>
        snapshotReader.IsUserCoordinatorOfTeamAsync(teamId, userId, cancellationToken);

    public Task<TeamMemberRemovalResult> RemoveMemberAsync(
        Guid teamId,
        Guid userId,
        Guid actorUserId,
        CancellationToken cancellationToken = default) =>
        RemoveMemberCoreAsync(teamId, userId, actorUserId, null, cancellationToken);

    private async Task<TeamMemberRemovalResult> RemoveMemberCoreAsync(
        Guid teamId, Guid userId, Guid actorUserId, Guid? mergeTargetUserId, CancellationToken cancellationToken)
    {
        var team = await repo.GetByIdAsync(teamId, cancellationToken);
        if (team is null)
            return RefuseMemberRemoval(teamId, userId, actorUserId, "Teams_RemoveMember_TeamMissing", $"Team {teamId} not found");

        if (team.IsSystemTeam)
            return RefuseMemberRemoval(teamId, userId, actorUserId, "Teams_RemoveMember_SystemTeam", "Cannot remove members from system team manually");

        var canApprove = await snapshotReader.CanUserApproveRequestsForTeamAsync(teamId, actorUserId, cancellationToken);
        if (!canApprove)
            return RefuseMemberRemoval(teamId, userId, actorUserId, "Teams_RemoveMember_Permission", "User does not have permission to remove members from this team");

        var member = await repo.FindActiveMemberForMutationAsync(teamId, userId, cancellationToken);
        if (member is null)
            return RefuseMemberRemoval(teamId, userId, actorUserId, "Teams_RemoveMember_NotMember", "User is not a member of this team");

        TeamMember? mergeTarget = null;
        if (mergeTargetUserId is { } targetUserId)
        {
            if (targetUserId == userId)
                throw new InvalidOperationException("Cannot merge a membership into itself");
            mergeTarget = await repo.FindActiveMemberForMutationAsync(teamId, targetUserId, cancellationToken);
            if (mergeTarget is null)
            {
                var addition = await AddMemberToTeamAsync(teamId, targetUserId, actorUserId, cancellationToken);
                mergeTarget = addition.Succeeded ? addition.Member! : throw new InvalidOperationException(addition.Refusal);
            }
        }
        var wasCoordinator = member.Role == TeamMemberRole.Coordinator;

        var removedAssignments = await membershipOutbox.MarkMemberLeftWithOutboxAsync(member, mergeTarget?.Id, cancellationToken);

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamMemberRemoved, nameof(Team), teamId,
            $"Member removed from {team.Name}",
            actorUserId,
            relatedEntityId: userId, relatedEntityType: AuditSubjectTypes.User);

        if (mergeTarget is not null && (wasCoordinator || removedAssignments.Count > 0))
        {
            await auditLogService.LogAsync(
                TeamsAuditActions.TeamMemberRoleChanged, nameof(Team), teamId,
                $"Folded {removedAssignments.Count} role assignment(s) from merged account {userId}; source coordinator: {wasCoordinator}",
                actorUserId, mergeTarget.UserId, AuditSubjectTypes.User);
        }

        await membershipNotices.SendMemberRemovedNotificationAsync(team, userId, teamId, cancellationToken);

        // A removed coordinator's Coordinators eligibility is reconciled in the mutation, not left to the caller.
        if (wasCoordinator && mergeTarget is null)
            await SystemTeamSync.SyncMembershipForUserAsync(userId, SystemTeamType.Coordinators, cancellationToken);
        return new TeamMemberRemovalResult();
    }

    private TeamMemberRemovalResult RefuseMemberRemoval(Guid teamId, Guid userId, Guid actorId, string key, string message)
    {
        logger.LogWarning("Member removal refused for {UserId} from team {TeamId} by {ActorId}: {ErrorKey}", userId, teamId, actorId, key);
        return new TeamMemberRemovalResult(key, message);
    }

    public async Task<TeamJoinReviewResult> AddMemberToTeamAsync(
        Guid teamId,
        Guid targetUserId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdAsync(teamId, cancellationToken);
        if (team is null)
            return new(Refusal: $"Team {teamId} not found");

        if (team.IsSystemTeam)
            return new(Refusal: "Cannot add members to system teams manually");

        var resolvedUserId = await snapshotReader.ResolveMembershipTargetAsync(targetUserId, cancellationToken);
        if (resolvedUserId is null)
            return new(Refusal: TeamSnapshotReader.UnavailableMembershipTargetMessage);
        targetUserId = resolvedUserId.Value;
        var isExisting = await repo.IsActiveMemberAsync(teamId, targetUserId, cancellationToken);
        if (isExisting)
            return new(Refusal: "User is already a member of this team");

        // A pending request is resolved by approving it with the membership.
        var pendingRequest = await repo.FindUserPendingRequestAsync(teamId, targetUserId, cancellationToken);
        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = targetUserId,
            Role = TeamMemberRole.Member,
            JoinedAt = clock.GetCurrentInstant()
        };

        var outboxEvent = await membershipOutbox.BuildOutboxEventAsync(
            member.Id, teamId, targetUserId, GoogleSyncOutboxEventTypes.AddUserToTeamResources, cancellationToken);

        bool success;
        try
        {
            if (pendingRequest is not null)
            {
                var tracked = await repo.FindRequestForMutationAsync(pendingRequest.Id, cancellationToken);
                if (tracked is not null)
                {
                    if (tracked.Status == TeamJoinRequestStatus.Pending)
                    {
                        tracked.Status = TeamJoinRequestStatus.Approved;
                        tracked.ReviewedByUserId = actorUserId;
                        tracked.ReviewNotes = "Added directly by team manager";
                        tracked.ResolvedAt = clock.GetCurrentInstant();
                    }
                    success = await membershipOutbox.ApproveRequestWithMemberAndOutboxAsync(
                        tracked, member, outboxEvent, cancellationToken);
                }
                else
                {
                    success = await membershipOutbox.TryAddMemberWithOutboxAsync(member, outboxEvent, cancellationToken);
                }
            }
            else
            {
                success = await membershipOutbox.TryAddMemberWithOutboxAsync(member, outboxEvent, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add user {UserId} to team {TeamId}", targetUserId, teamId);
            throw;
        }

        if (!success)
            return new(Refusal: "User is already a member of this team");

        // The mutation is committed; finish its notices even if the browser disconnects.
        cancellationToken = CancellationToken.None;

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamMemberAdded, nameof(Team), teamId,
            $"Member added to {team.Name}",
            actorUserId,
            relatedEntityId: targetUserId, relatedEntityType: AuditSubjectTypes.User);

        await membershipNotices.SendAddedToTeamEmailAsync(targetUserId, team, cancellationToken);

        return new(Member: member);
    }

    public async Task SetMemberRoleAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var newRole = await repo.SetMemberRoleAsync(teamId, userId, role, cancellationToken);
        if (newRole is null)
            return;

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamMemberRoleChanged, nameof(Team), teamId,
            $"Set member role to {role}",
            actorUserId,
            relatedEntityId: userId, relatedEntityType: AuditSubjectTypes.User);
    }

    public Task<TeamRoleDefinitionMutationResult> CreateRoleDefinitionAsync(
        Guid teamId, string name, string? description, int slotCount,
        List<SlotPriority> priorities, int sortOrder, RolePeriod period, Guid actorUserId,
        bool isPublic = true, int? estimatedHours = null, CancellationToken cancellationToken = default) =>
        RoleDefinitions.CreateRoleDefinitionAsync(teamId, name, description, slotCount, priorities,
            sortOrder, period, actorUserId, isPublic, estimatedHours, cancellationToken);

    public Task<TeamRoleDefinitionMutationResult> UpdateRoleDefinitionAsync(
        Guid roleDefinitionId, string name, string? description, int slotCount,
        List<SlotPriority> priorities, int sortOrder, bool isManagement, RolePeriod period, Guid actorUserId,
        bool isPublic = true, bool canToggleManagement = true, int? estimatedHours = null,
        CancellationToken cancellationToken = default) =>
        RoleDefinitions.UpdateRoleDefinitionAsync(roleDefinitionId, name, description, slotCount, priorities,
            sortOrder, isManagement, period, actorUserId, isPublic, canToggleManagement, estimatedHours, cancellationToken);

    public Task<TeamRoleDefinitionMutationResult> DeleteRoleDefinitionAsync(
        Guid roleDefinitionId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        RoleDefinitions.DeleteRoleDefinitionAsync(roleDefinitionId, actorUserId, cancellationToken);

    public Task<TeamRoleDefinitionMutationResult> ToggleRoleIsManagementAsync(
        Guid roleDefinitionId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        RoleDefinitions.ToggleRoleIsManagementAsync(roleDefinitionId, actorUserId, cancellationToken);

    public Task<IReadOnlyList<TeamRoleDefinitionSnapshot>> GetRoleDefinitionsAsync(
        Guid teamId, CancellationToken cancellationToken = default) =>
        snapshotReader.GetRoleDefinitionsAsync(teamId, cancellationToken);

    private async Task<(TeamRoleDefinition? Definition, Team? Team, string? Refusal)> ResolveRoleAssignmentManagementAsync(
        Guid roleDefinitionId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var definition = await repo.FindRoleDefinitionForMutationAsync(roleDefinitionId, cancellationToken);
        if (definition is null)
            return (null, null, $"Role definition {roleDefinitionId} not found");
        var team = await snapshotReader.GetRoleDefinitionTeamAsync(definition, cancellationToken);

        var canManage = await snapshotReader.CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            return (null, null, "User does not have permission to manage role assignments for this team");

        return (definition, team, null);
    }

    public async Task<TeamRoleAssignmentMutationResult> AssignToRoleAsync(
        Guid roleDefinitionId, Guid targetUserId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var (definition, team, refusal) = await ResolveRoleAssignmentManagementAsync(
            roleDefinitionId, actorUserId, cancellationToken);
        if (definition is null || team is null)
            return new(Refusal: refusal);

        var resolvedUserId = await snapshotReader.ResolveMembershipTargetAsync(targetUserId, cancellationToken);
        if (resolvedUserId is null)
            return new(Refusal: TeamSnapshotReader.UnavailableMembershipTargetMessage);
        targetUserId = resolvedUserId.Value;
        var existingMember = await repo.FindActiveMemberForMutationAsync(definition.TeamId, targetUserId, cancellationToken);
        var now = clock.GetCurrentInstant();

        TeamMember? autoAddMember = null;
        GoogleSyncOutboxEvent? outboxEvent = null;
        if (existingMember is null)
        {
            if (team.IsSystemTeam)
                return new(Refusal: "Cannot add members to system teams via role assignment; only existing members can be assigned to roles on system teams.");

            autoAddMember = new TeamMember
            {
                Id = Guid.NewGuid(),
                TeamId = definition.TeamId,
                UserId = targetUserId,
                Role = TeamMemberRole.Member,
                JoinedAt = now
            };
            outboxEvent = await membershipOutbox.BuildOutboxEventAsync(
                autoAddMember.Id, definition.TeamId, targetUserId,
                GoogleSyncOutboxEventTypes.AddUserToTeamResources, cancellationToken);
        }

        var result = await membershipOutbox.AssignToRoleWithOutboxAsync(
            roleDefinitionId,
            targetUserId,
            actorUserId,
            autoAddMember,
            outboxEvent,
            promoteToCoordinator: definition.IsManagement,
            now,
            cancellationToken);

        if (!result.Succeeded)
            return result;

        if (autoAddMember is not null)
        {
            await auditLogService.LogAsync(
                TeamsAuditActions.TeamMemberAdded, nameof(Team), definition.TeamId,
                $"Auto-added to {team.Name} via role assignment",
                actorUserId,
                relatedEntityId: targetUserId, relatedEntityType: AuditSubjectTypes.User);
        }

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamRoleAssigned, nameof(TeamRoleDefinition), roleDefinitionId,
            $"Assigned to role '{definition.Name}' in {team.Name}",
            actorUserId,
            relatedEntityId: targetUserId, relatedEntityType: AuditSubjectTypes.User);

        // Management roles promote: reconcile Coordinators in the mutation.
        await SystemTeamSync.SyncMembershipForUserAsync(targetUserId, SystemTeamType.Coordinators, cancellationToken);

        return result;
    }

    public async Task<TeamRoleAssignmentMutationResult> UnassignFromRoleAsync(
        Guid roleDefinitionId, Guid teamMemberId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var (definition, team, refusal) = await ResolveRoleAssignmentManagementAsync(
            roleDefinitionId, actorUserId, cancellationToken);
        if (definition is null || team is null)
            return new(Refusal: refusal);

        var result = await repo.UnassignFromRoleAsync(
            roleDefinitionId, teamMemberId, cancellationToken);
        if (!result.Succeeded)
            return result;
        var targetUserId = result.Assignment!.TeamMember.UserId;

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamRoleUnassigned, nameof(TeamRoleDefinition), roleDefinitionId,
            $"Unassigned from role '{definition.Name}' in {team.Name}",
            actorUserId,
            relatedEntityId: targetUserId, relatedEntityType: AuditSubjectTypes.User);

        // Losing a management role can demote: reconcile Coordinators in the mutation.
        await SystemTeamSync.SyncMembershipForUserAsync(targetUserId, SystemTeamType.Coordinators, cancellationToken);
        return result;
    }

    public Task<IReadOnlyList<Guid>> GetUserCoordinatedTeamIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        repo.GetUserCoordinatorTeamIdsAsync(userId, cancellationToken);

    public async Task EnqueueGoogleResyncForUserTeamsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetCurrentInstant();
        var memberships = await repo.GetActiveMembershipsForGoogleResyncAsync(userId, cancellationToken);
        var outboxEvents = memberships
            .Select(membership => new GoogleSyncOutboxEvent
            {
                Id = Guid.NewGuid(),
                EventType = GoogleSyncOutboxEventTypes.AddUserToTeamResources,
                TeamId = membership.TeamId,
                UserId = userId,
                OccurredAt = now,
                DeduplicationKey = $"{membership.TeamMemberId}:{GoogleSyncOutboxEventTypes.AddUserToTeamResources}:resync:{now}"
            })
            .ToList();

        await GoogleSyncOutboxService.AddRangeAsync(outboxEvents, cancellationToken);
        var count = outboxEvents.Count;
        if (count > 0)
        {
            logger.LogInformation(
                "Enqueued {Count} re-sync events for user {UserId} after Google email change",
                count, userId);
        }
    }

    async Task<TeamInfo> ITeamSeeding.CreateTeamAsync(
        string name,
        string? description,
        bool requiresApproval,
        Guid? parentTeamId,
        string? googleGroupPrefix,
        bool isHidden,
        CancellationToken cancellationToken)
    {
        var team = await CreateTeamAsync(
            name, description, requiresApproval, parentTeamId, googleGroupPrefix, isHidden,
            cancellationToken);
        return await GetTeamAsync(team.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Team {team.Id} missing from the read model after creation");
    }

    async Task ITeamSeeding.UpdateTeamAsync(
        Guid teamId,
        string name,
        string? description,
        bool requiresApproval,
        bool isActive,
        Guid? parentTeamId,
        string? googleGroupPrefix,
        string? customSlug,
        bool? hasBudget,
        bool? isHidden,
        bool? isSensitive,
        bool? isPromotedToDirectory,
        bool? earlyEntryEnabled,
        CancellationToken cancellationToken)
    {
        var result = await UpdateTeamAsync(
            teamId, name, description, requiresApproval, isActive, parentTeamId, googleGroupPrefix,
            customSlug, hasBudget, isHidden, isSensitive, isPromotedToDirectory, earlyEntryEnabled,
            cancellationToken);
        result.RequireSuccess();
    }

    Task ITeamSeeding.AddSeededMemberAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Instant joinedAt,
        CancellationToken cancellationToken) =>
        AddSeededMemberAsync(teamId, userId, role, joinedAt, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamsWithParentsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken = default)
    {
        // Deactivated teams included, as the entity-returning sibling promises its callers.
        var teamsById = await LoadTeamsByIdAsync(cancellationToken);
        var result = new Dictionary<Guid, TeamInfo>();
        foreach (var teamId in teamIds)
        {
            if (!teamsById.TryGetValue(teamId, out var team))
                continue;
            result[teamId] = team;
            if (team.ParentTeamId is { } parentTeamId
                && teamsById.TryGetValue(parentTeamId, out var parent))
            {
                result[parentTeamId] = parent;
            }
        }
        return result;
    }

    public async Task<TeamMember> AddSeededMemberAsync(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        Instant joinedAt,
        CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdAsync(teamId, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
            throw new InvalidOperationException("AddSeededMemberAsync cannot target system teams");

        var existing = await repo.IsActiveMemberAsync(teamId, userId, cancellationToken);
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

        await repo.AddMemberAsync(member, cancellationToken);

        return member;
    }

    public async Task<bool> PermanentlyDeleteTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        await adminAuthorization.RequireCurrentUserIsAdminAsync(cancellationToken);

        var team = await repo.GetByIdAsync(teamId, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
            throw new InvalidOperationException("Cannot permanently delete system team");

        var hasActiveChildren = await repo.HasActiveChildrenAsync(teamId, cancellationToken);
        if (hasActiveChildren)
            throw new InvalidOperationException("Cannot permanently delete a team that has active sub-teams. Remove or reassign sub-teams first.");

        // Restrict FK: pre-check for a clear message instead of a raw violation.
        var resources = await TeamResourceService.GetTeamResourcesAsync(teamId, cancellationToken);
        if (resources.Count > 0)
            throw new InvalidOperationException("Cannot permanently delete a team that has Google resources linked. Unlink resources first.");

        return await repo.PermanentlyDeleteTeamAsync(teamId, cancellationToken);
    }

    public Task<IReadOnlyDictionary<Guid, string>> GetManagementRoleNamesByTeamIdsAsync(
        IEnumerable<Guid> teamIds,
        CancellationToken cancellationToken = default) =>
        repo.GetPublicManagementRoleNamesByTeamIdsAsync(teamIds.ToList(), cancellationToken);

    public Task<IReadOnlyList<AdminTeamSummary>> GetAdminTeamListAsync(CancellationToken cancellationToken = default) =>
        snapshotReader.GetAdminTeamListAsync(cancellationToken);

    // CachingTeamService owns the cache.
    public void InvalidateActiveTeamsCache() { }

    public Task<int> RevokeAllMembershipsAsync(Guid userId, CancellationToken cancellationToken = default) =>
        repo.RevokeAllMembershipsAsync(userId, clock.GetCurrentInstant(), cancellationToken);

    public async Task ReassignAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Guid actorUserId,
        Instant updatedAt,
        CancellationToken cancellationToken)
    {
        // Ends source memberships through the ordinary removal/outbox path, folding roles into the survivor in the
        // same save. System teams reconcile separately.
        var sourceMemberships = await GetUserTeamMembershipsAsync(sourceUserId, cancellationToken);
        var sourceTeamsById = await repo.GetByIdsWithParentsAsync(
            sourceMemberships.Select(m => m.TeamId).Distinct().ToList(), cancellationToken);

        foreach (var membership in sourceMemberships)
        {
            if (sourceTeamsById.TryGetValue(membership.TeamId, out var team) && team.IsSystemTeam)
                continue;

            var removal = await RemoveMemberCoreAsync(
                membership.TeamId, sourceUserId, actorUserId, targetUserId, cancellationToken);
            if (removal.ErrorKey is not null)
                throw new InvalidOperationException(removal.ErrorMessage);
        }

        await repo.ReassignActiveJoinRequestsAsync(sourceUserId, targetUserId, cancellationToken);

        await repo.ReassignEarlyEntryGrantsAsync(sourceUserId, targetUserId, cancellationToken);
        // Always retry both reconciles, even when an earlier pass ended every source membership before failing here.
        await SystemTeamSync.SyncMembershipForUserAsync(sourceUserId, SystemTeamType.Coordinators, cancellationToken);
        await SystemTeamSync.SyncMembershipForUserAsync(targetUserId, SystemTeamType.Coordinators, cancellationToken);
    }

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        dataExport.ContributeForUserAsync(userId, ct);

    /// <summary>Join requests are hard-deleted: they carry the applicant's free-text message.</summary>
    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        await RevokeAllMembershipsAsync(userId, ct);
        await repo.DeleteJoinRequestsForUserAsync(userId, ct);
        await DeleteEarlyEntryGrantsForUserAsync(userId, ct);

        // No cache call: Gdpr reaches this through CachingTeamService, which evicts.
    }

    public Task<IReadOnlyList<EarlyEntryGrant>> GetEarlyEntriesAsync(CancellationToken ct) =>
        EarlyEntry.GetEarlyEntriesAsync(ct);

    public Task<IReadOnlyList<TeamEarlyEntryGrantInfo>> GetEarlyEntryGrantsForTeamAsync(Guid teamId, CancellationToken ct = default) =>
        EarlyEntry.GetEarlyEntryGrantsForTeamAsync(teamId, ct);

    public Task<TeamEarlyEntryMutationResult> AddEarlyEntryGrantAsync(Guid teamId, Guid userId, LocalDate entryDate, string projectName, Guid actorUserId, CancellationToken ct = default) =>
        EarlyEntry.AddEarlyEntryGrantAsync(teamId, userId, entryDate, projectName, actorUserId, ct);

    public Task<TeamEarlyEntryMutationResult> EditEarlyEntryGrantAsync(Guid teamId, Guid grantId, LocalDate entryDate, string projectName, Guid actorUserId, CancellationToken ct = default) =>
        EarlyEntry.EditEarlyEntryGrantAsync(teamId, grantId, entryDate, projectName, actorUserId, ct);

    public Task RemoveEarlyEntryGrantAsync(Guid teamId, Guid grantId, Guid actorUserId, CancellationToken ct = default) =>
        EarlyEntry.RemoveEarlyEntryGrantAsync(teamId, grantId, actorUserId, ct);

    private Task DeleteEarlyEntryGrantsForUserAsync(Guid userId, CancellationToken ct) =>
        EarlyEntry.DeleteEarlyEntryGrantsForUserAsync(userId, ct);

    private Task<IReadOnlyDictionary<Guid, TeamInfo>> LoadTeamsByIdAsync(CancellationToken ct = default) =>
        snapshotReader.LoadTeamsByIdAsync(ct);

    public Task<IReadOnlyDictionary<Guid, TeamInfo>> ResolveTeamMembersAsync(
        IReadOnlyDictionary<Guid, TeamReadSnapshot> teams, CancellationToken ct = default) =>
        snapshotReader.ResolveTeamMembersAsync(teams, ct);

    public Task<IReadOnlyDictionary<Guid, TeamReadSnapshot>> GetTeamSnapshotsAsync(CancellationToken ct = default) =>
        snapshotReader.GetTeamSnapshotsAsync(ct);

    public async Task<IReadOnlyList<TeamRoleReconciliationMembership>> GetActiveMembershipsForRoleReconciliationAsync(
        CancellationToken cancellationToken = default)
    {
        var memberships = await repo.GetActiveMembershipsForRoleReconciliationAsync(cancellationToken);
        return memberships
            .Select(member => new TeamRoleReconciliationMembership(
                member.Id,
                member.UserId,
                member.TeamId,
                member.Team.Name,
                member.Role,
                member.Team.SystemTeamType,
                member.RoleAssignments.Any(assignment => assignment.TeamRoleDefinition.IsManagement)))
            .ToList();
    }

    public async Task<IReadOnlyList<Guid>> ApplyMemberRoleChangesAsync(
        IReadOnlyCollection<(Guid TeamMemberId, TeamMemberRole Role)> changes,
        CancellationToken cancellationToken = default)
    {
        if (changes.Count == 0)
            return [];
        return await repo.ApplyMemberRoleChangesAsync(changes, cancellationToken);
    }

    public async Task<bool> ApplySystemTeamMembershipDeltaAsync(
        Guid teamId,
        IReadOnlyCollection<Guid> userIdsToAdd,
        IReadOnlyCollection<Guid> userIdsToRemove,
        Instant now,
        CancellationToken cancellationToken = default)
    {
        return await repo.ApplySystemTeamMembershipDeltaAsync(
            teamId, userIdsToAdd, userIdsToRemove, now, cancellationToken);
    }

    public Task<Dictionary<string, Guid[]>> GetExpectedAsync(
        string? groupKey = null,
        CancellationToken ct = default) =>
        googleGroupMemberships.GetExpectedAsync(groupKey, ct);

}
