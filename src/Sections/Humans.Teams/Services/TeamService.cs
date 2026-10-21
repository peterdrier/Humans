using System.Globalization;
using System.Resources;
using Humans.GoogleIntegration.Contracts;
using Humans.Auth.Contracts;
using System.Transactions;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Base.Extensions;
using Humans.Base.Threading;
using Humans.Base.Helpers;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces.Caching;
using Humans.EarlyEntry.Contracts;
using Humans.Email.Contracts;
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
    INotificationMeterCacheInvalidator notificationMeterInvalidator,
    IShiftAuthorizationInvalidator shiftAuthInvalidator,
    IAdminAuthorizationService adminAuthorization,
    IServiceProvider serviceProvider,
    IClock clock,
    ILogger<TeamService> logger) : ITeamManagementService, ITeamSeeding, IGoogleGroupMembershipSource, IUserDataContributor, IUserMerge, IEarlyEntryProvider
{
    private static readonly ResourceManager NoticeResources = new(typeof(TeamsResource));
    private static readonly TrackedLock JoinRequestLock = new("Teams.JoinRequest");
    private const int MaxTeamSlugLength = 256;
    private static readonly HashSet<string> ReservedTeamSlugs = new(StringComparer.Ordinal)
    {
        "roster", "birthdays", "map", "my", "sync", "summary", "create", "search", "tree"
    };

    internal const string TeamMemberships = "TeamMemberships";
    internal const string TeamJoinRequests = "TeamJoinRequests";
    internal const string TeamEarlyEntry = "TeamEarlyEntry";

    // Lazy resolution — UserService injects ITeamManagement, closing the cycle.
    private ITeamResourceService TeamResourceService
        => serviceProvider.GetRequiredService<ITeamResourceService>();

    private IRoleAssignmentService RoleAssignmentService
        => serviceProvider.GetRequiredService<IRoleAssignmentService>();

    private IEmailService EmailService
        => serviceProvider.GetRequiredService<IEmailService>();

    private TeamsEmails EmailMessages
        => serviceProvider.GetRequiredService<TeamsEmails>();

    private ISystemTeamSync SystemTeamSync
        => serviceProvider.GetRequiredService<ISystemTeamSync>();

    private IUserService UserService
        => serviceProvider.GetRequiredService<IUserService>();

    // GoogleIntegration's identity read resolves IUserService too, so it is lazy for the same reason.
    private IGoogleIntegrationService GoogleIdentities
        => serviceProvider.GetRequiredService<IGoogleIntegrationService>();

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
        var baseSlug = SlugHelper.GenerateSlug(name);
        if (baseSlug.Length == 0)
            baseSlug = "team";
        var now = clock.GetCurrentInstant();

        if (ReservedTeamSlugs.Contains(baseSlug))
            throw new InvalidOperationException($"The team name '{name}' conflicts with a reserved route");

        if (parentTeamId.HasValue)
        {
            var parent = await repo.GetByIdAsync(parentTeamId.Value, cancellationToken)
                ?? throw new InvalidOperationException($"Parent team {parentTeamId.Value} not found");

            if (parent.IsSystemTeam)
                throw new InvalidOperationException("System teams cannot be parents");

            if (parent.ParentTeamId.HasValue)
                throw new InvalidOperationException("Cannot nest more than one level — the parent team already has a parent");
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
            if (persisted)
            {
                logger.LogInformation("Created team {TeamName} with slug {Slug}", name, slug);
                return team;
            }

            // Slug/custom_slug unique-constraint race (Npgsql 23505 → false). Retry next suffix.
            logger.LogDebug(
                "Slug collision for '{Slug}' at persist, retrying (attempt {Attempt})",
                slug, attempt + 1);
        }

        throw new InvalidOperationException($"Could not generate unique slug for team '{name}' after 10 attempts");
    }

    public async Task<TeamInfo?> GetTeamBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        var teamsById = await LoadTeamsByIdAsync(cancellationToken);
        return TeamSlugResolver.Find(teamsById, slug);
    }

    public async Task<TeamInfo?> GetTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        var teamsById = await LoadTeamsByIdAsync(cancellationToken);
        return teamsById.GetValueOrDefault(teamId);
    }

    public async Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamsAsync(
        CancellationToken cancellationToken = default) =>
        await LoadTeamsByIdAsync(cancellationToken);

    // Team search is served from the cached TeamInfo snapshot in CachingTeamService — it must
    // never hit the DB. Reaching the inner service means a DI mistake. Mirrors
    // UserService.SearchUsersAsync (search is cache-only; there is no repository search).
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserTeamMembershipInfo>> GetUserTeamMembershipsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var memberships = await repo.GetActiveByUserIdAsync(userId, cancellationToken);
        return memberships
            .Select(m => new UserTeamMembershipInfo(
                m.Id, m.TeamId, m.Team.Name, m.Team.Slug, m.Team.IsSystemTeam, m.Role, m.JoinedAt))
            .ToList();
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

            var parent = await repo.GetByIdAsync(parentTeamId.Value, cancellationToken);
            if (parent is null) return new(ErrorMessage: $"Parent team {parentTeamId.Value} not found");

            if (parent.IsSystemTeam)
                return new(ErrorMessage: "System teams cannot be parents");

            if (parent.ParentTeamId.HasValue)
                return new(ErrorMessage: "Cannot nest more than one level — the parent team already has a parent");
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

        var becomingChild = parentTeamId.HasValue && !team.ParentTeamId.HasValue;
        var parentChanging = parentTeamId != team.ParentTeamId;
        var usersNeedingShiftAuthorizationInvalidation = parentChanging
            ? await repo.GetUserIdsWithManagementOnTeamAsync(teamId, cancellationToken)
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
            // Docs/health.md §4: only a global Admin writes IsSensitive. The gate sits here rather
            // than in the controller so every caller of this service is held to it. Gating on the
            // change, not on the argument, keeps a caller that passes the flag's current value
            // (the dev fixture seeders on ITeamSeeding do) out of it — a no-op is not a write.
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

        InvalidateShiftAuthorization(usersNeedingShiftAuthorizationInvalidation);

        if (becomingChild && usersNeedingShiftAuthorizationInvalidation.Count > 0)
        {
            foreach (var userId in usersNeedingShiftAuthorizationInvalidation)
                await SystemTeamSync.SyncMembershipForUserAsync(userId, SystemTeamType.Coordinators, cancellationToken);
        }

        logger.LogInformation("Updated team {TeamId} ({TeamName})", teamId, name);

        return new(Team: team);
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
        var normalizedCallsToAction = callsToAction
            .Where(c => !string.IsNullOrWhiteSpace(c.Text) && !string.IsNullOrWhiteSpace(c.Url))
            .Select(c => new CallToAction { Text = c.Text!.Trim(), Url = c.Url!.Trim(), Style = c.Style })
            .ToList();

        try
        {
            var team = await repo.FindForMutationAsync(teamId, cancellationToken)
                ?? throw new TeamPageRuleException($"Team {teamId} not found");

            if (normalizedCallsToAction.Count > 3)
                throw new TeamPageRuleException("A team can have at most 3 calls to action.");

            if (normalizedCallsToAction.Count(c => c.Style == CallToActionStyle.Primary) > 1)
                throw new TeamPageRuleException("Only one primary call to action is allowed.");

            if (normalizedCallsToAction.Any(c => !IsSafeCallToActionUrl(c.Url)))
                throw new TeamPageRuleException("Calls to action require a relative URL or an HTTP, HTTPS or mailto URL.");

            var canBePublic = !team.IsSystemTeam && !team.ParentTeamId.HasValue;

            if (isPublicPage && !canBePublic)
                throw new TeamPageRuleException("Only departments (non-system, top-level teams) can be made public.");

            var now = clock.GetCurrentInstant();
            team.PageContent = pageContent;
            team.CallsToAction = normalizedCallsToAction;
            team.IsPublicPage = isPublicPage;
            team.ShowCoordinatorsOnPublicPage = canBePublic && isPublicPage && showCoordinatorsOnPublicPage;
            team.PageContentUpdatedAt = now;
            team.PageContentUpdatedByUserId = updatedByUserId;
            team.UpdatedAt = now;

            var conflict = await repo.UpdateTeamAsync(team, cancellationToken);
            if (conflict != TeamUpdateConflict.None)
            {
                logger.LogWarning("Team page update rejected for team {TeamId}: {Conflict}", teamId, conflict);
                return new TeamPageUpdateResult(false, null);
            }

            await auditLogService.LogAsync(
                AuditAction.TeamPageContentUpdated, nameof(Team), teamId,
                $"Team page content updated. Public: {isPublicPage}",
                updatedByUserId);

            return TeamPageUpdateResult.Success();
        }
        catch (TeamPageRuleException ex)
        {
            logger.LogWarning("Team page update rejected for team {TeamId} by user {UserId}: {Reason}", teamId, updatedByUserId, ex.Message);
            return TeamPageUpdateResult.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to update team page for team {TeamId} by user {UserId}", teamId, updatedByUserId);
            return new TeamPageUpdateResult(false, null);
        }
    }

    // Shared by writes and the page projection so unsafe legacy values never become links.
    internal static bool IsSafeCallToActionUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && !url.Any(char.IsControl)
        && Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri)
        && (!uri.IsAbsoluteUri || uri.Scheme is "http" or "https" or "mailto");

    private sealed class TeamPageRuleException : InvalidOperationException
    {
        public TeamPageRuleException() { }
        public TeamPageRuleException(string? message) : base(message) { }
        public TeamPageRuleException(string? message, Exception? innerException) : base(message, innerException) { }
    }

    public async Task<TeamWithGroupResult> CreateTeamWithGoogleGroupAsync(
        string name,
        string? description,
        bool requiresApproval,
        Guid? parentTeamId = null,
        string? googleGroupPrefix = null,
        bool isHidden = false,
        CancellationToken cancellationToken = default)
    {
        var team = await CreateTeamAsync(
            name, description, requiresApproval, parentTeamId, googleGroupPrefix, isHidden, cancellationToken);

        string? groupWarning = null;
        if (!string.IsNullOrEmpty(googleGroupPrefix))
        {
            try
            {
                await GoogleSyncService.EnsureTeamGroupAsync(team.Id, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                // Compensation: clear the prefix so the team never points at a
                // group that does not exist; the admin can retry from team edit.
                logger.LogError(ex, "Failed to create Google Group for new team {TeamId}, clearing prefix", team.Id);
                var compensation = await UpdateTeamAsync(
                    team.Id, team.Name, team.Description, team.RequiresApproval, team.IsActive,
                    team.ParentTeamId, googleGroupPrefix: null, cancellationToken: cancellationToken);
                compensation.RequireSuccess();
                groupWarning = $"Team created but Google Group setup failed: {ex.Message}. The group prefix has been cleared.";
            }
        }

        return new TeamWithGroupResult(team, groupWarning);
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

        string? groupWarning;
        try
        {
            var groupResult = await GoogleSyncService.EnsureTeamGroupAsync(teamId, cancellationToken: cancellationToken);
            groupWarning = groupResult.Success
                ? null
                : groupResult.RequiresConfirmation
                    ? groupResult.WarningMessage ?? "Confirmation required for group reactivation."
                    : groupResult.ErrorMessage ?? "Google Group linking failed.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to sync Google Group for team {TeamId}", teamId);
            groupWarning = $"Team updated but Google Group setup failed: {ex.Message}";
        }

        return result with { GroupWarning = groupWarning };
    }

    public async Task DeleteTeamAsync(Guid teamId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdAsync(teamId, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
            throw new InvalidOperationException("Cannot delete system team");

        var hasActiveChildren = await repo.HasActiveChildrenAsync(teamId, cancellationToken);
        if (hasActiveChildren)
            throw new InvalidOperationException("Cannot deactivate a team that has active sub-teams. Remove or reassign sub-teams first.");

        var now = clock.GetCurrentInstant();

        var closedUsers = await repo.DeactivateTeamAsync(teamId, now, cancellationToken);
        foreach (var userId in closedUsers)
        {
            await auditLogService.LogAsync(
                AuditAction.TeamMemberRemoved, nameof(Team), teamId,
                $"Membership ended when {team.Name} was deactivated",
                actorUserId, relatedEntityId: userId, relatedEntityType: nameof(User));
        }

        // GoogleResource.IsActive flipped later by Google reconciliation tick (deferred).

        logger.LogInformation(
            "Deactivated team {TeamId} ({TeamName}); closed {MemberCount} memberships",
            teamId, team.Name, closedUsers.Count);
    }

    public async Task<TeamJoinResult> JoinTeamAsync(
        Guid teamId,
        Guid userId,
        string? message,
        CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdWithRelationsAsync(teamId, cancellationToken);
        if (team is null)
            return new TeamJoinResult(ErrorKey: "Teams_NotFound");

        // The join policy is the team's decision, not the caller's: approval-required
        // teams queue a request, open teams join immediately.
        if (team.RequiresApproval)
        {
            var result = await RequestToJoinTeamAsync(teamId, userId, message, cancellationToken);
            return result.ErrorKey is null
                ? new TeamJoinResult(TeamJoinOutcome.RequestSubmitted)
                : new TeamJoinResult(ErrorKey: result.ErrorKey);
        }

        var directResult = await JoinTeamDirectlyAsync(teamId, userId, cancellationToken);
        return directResult.ErrorKey is null
            ? new TeamJoinResult(TeamJoinOutcome.Joined)
            : new TeamJoinResult(ErrorKey: directResult.ErrorKey);
    }

    public async Task<TeamJoinRequestResult> RequestToJoinTeamAsync(
        Guid teamId,
        Guid userId,
        string? message,
        CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdWithRelationsAsync(teamId, cancellationToken);
        if (team is null)
            return new TeamJoinRequestResult(ErrorKey: "Teams_NotFound");

        if (!team.IsActive)
            return new TeamJoinRequestResult(ErrorKey: "Teams_NotFound");

        if (team.IsSystemTeam)
            return new TeamJoinRequestResult(ErrorKey: "Teams_Team_CannotJoinSystem");

        if (team.IsHidden)
            return new TeamJoinRequestResult(ErrorKey: "Teams_CannotJoinHidden");

        if (!team.RequiresApproval)
            return new TeamJoinRequestResult(ErrorKey: "Teams_JoinPolicyChanged");

        using var joinRequest = await JoinRequestLock.AcquireAsync(logger, cancellationToken);
        var existingRequest = await repo.FindUserPendingRequestAsync(teamId, userId, cancellationToken);
        if (existingRequest is not null)
            return new TeamJoinRequestResult(ErrorKey: "Teams_Team_AlreadyPendingRequest");

        var teamInfo = await GetTeamAsync(teamId, cancellationToken);
        var isMember = teamInfo is { IsActive: true } && teamInfo.Members.Any(m => m.UserId == userId);
        if (isMember)
            return new TeamJoinRequestResult(ErrorKey: "Teams_Team_AlreadyMember");

        var request = new TeamJoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Message = message,
            RequestedAt = clock.GetCurrentInstant()
        };

        await repo.AddRequestAsync(request, cancellationToken);
        joinRequest.Dispose();

        logger.LogInformation("User {UserId} requested to join team {TeamId}", userId, teamId);
        await SendTeamJoinRequestSubmittedNotificationAsync(team, userId, cancellationToken);

        return new TeamJoinRequestResult(request);
    }

    public async Task<TeamDirectJoinResult> JoinTeamDirectlyAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdWithRelationsAsync(teamId, cancellationToken);
        if (team is null)
            return new TeamDirectJoinResult(ErrorKey: "Teams_NotFound");

        if (!team.IsActive)
            return new TeamDirectJoinResult(ErrorKey: "Teams_NotFound");

        if (team.IsSystemTeam)
            return new TeamDirectJoinResult(ErrorKey: "Teams_Team_CannotJoinSystem");

        if (team.IsHidden)
            return new TeamDirectJoinResult(ErrorKey: "Teams_CannotJoinHidden");

        if (team.RequiresApproval)
            return new TeamDirectJoinResult(ErrorKey: "Teams_JoinPolicyChanged");

        var existingMember = await repo.IsActiveMemberAsync(teamId, userId, cancellationToken);
        if (existingMember)
            return new TeamDirectJoinResult(ErrorKey: "Teams_Team_AlreadyMember");

        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = userId,
            Role = TeamMemberRole.Member,
            JoinedAt = clock.GetCurrentInstant()
        };

        var outboxEvent = await BuildOutboxEventAsync(
            member.Id, teamId, userId, GoogleSyncOutboxEventTypes.AddUserToTeamResources, cancellationToken);

        bool success;
        try
        {
            success = await TryAddMemberWithOutboxAsync(member, outboxEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add user {UserId} to team {TeamId} directly", userId, teamId);
            throw;
        }

        if (!success)
            return new TeamDirectJoinResult(ErrorKey: "Teams_Team_AlreadyMember");

        await auditLogService.LogAsync(
            AuditAction.TeamJoinedDirectly, nameof(Team), teamId,
            $"Joined {team.Name} directly",
            userId,
            relatedEntityId: userId, relatedEntityType: nameof(User));

        await SendAddedToTeamEmailAsync(userId, team, cancellationToken);
        await SendTeamMemberAddedNotificationAsync(team, userId, cancellationToken);

        return new TeamDirectJoinResult(member);
    }

    private async Task SendTeamJoinRequestSubmittedNotificationAsync(
        Team team,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var coordinatorUserIds = GetCoordinatorUserIds(team);
        if (coordinatorUserIds.Count == 0)
        {
            return;
        }

        try
        {
            var displayName = await GetDisplayNameAsync(userId, cancellationToken);
            var title = $"New join request for {team.Name}";
            await notificationService.SendAsync(
                NotificationSource.TeamJoinRequestSubmitted,
                NotificationClass.Actionable,
                NotificationPriority.Normal,
                title,
                coordinatorUserIds,
                body: $"{displayName} has requested to join {team.Name}.",
                actionUrl: $"/Teams/{team.Slug}/Members",
                actionLabel: "Review request",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch TeamJoinRequestSubmitted notification for team {TeamId}", team.Id);
        }
    }

    private async Task SendTeamMemberAddedNotificationAsync(
        Team team,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var coordinatorUserIds = GetCoordinatorUserIds(team);
        if (coordinatorUserIds.Count == 0)
        {
            return;
        }

        try
        {
            var displayName = await GetDisplayNameAsync(userId, cancellationToken);
            var title = $"{displayName} joined {team.Name}";
            await notificationService.SendAsync(
                NotificationSource.TeamMemberAdded,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                title,
                coordinatorUserIds,
                body: $"{displayName} has joined {team.Name}.",
                actionUrl: $"/Teams/{team.Slug}/Members",
                actionLabel: "View members",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch TeamMemberAdded notification for team {TeamId}", team.Id);
        }
    }

    private static IReadOnlyList<Guid> GetCoordinatorUserIds(Team team) =>
        team.Members
            .Where(member => member.Role == TeamMemberRole.Coordinator && member.LeftAt is null)
            .Select(member => member.UserId)
            .Distinct()
            .ToList();

    private async Task<CultureInfo> GetRecipientCultureAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var language = (await UserService.GetUserInfoAsync(userId, cancellationToken))?.PreferredLanguage;
            return CultureInfo.GetCultureInfo(language.IsSupportedCultureCode() ? language! : "en");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve notification language for user {UserId}; using English", userId);
            return CultureInfo.GetCultureInfo("en");
        }
    }

    private async Task<string> GetDisplayNameAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await UserService.GetUserInfoAsync(userId, cancellationToken);
        return user?.BurnerName ?? "Someone";
    }

    private async Task<bool> TryAddMemberWithOutboxAsync(
        TeamMember member,
        GoogleSyncOutboxEvent? outboxEvent,
        CancellationToken ct)
    {
        if (outboxEvent is null)
            return await repo.TryAddMemberAsync(member, ct);

        using var scope = CreateTransactionScope();
        var success = await repo.TryAddMemberAsync(member, ct);
        if (!success)
            return false;

        await GoogleSyncOutboxService.AddAsync(outboxEvent, ct);
        scope.Complete();
        return true;
    }

    private async Task<bool> ApproveRequestWithMemberAndOutboxAsync(
        TeamJoinRequest request,
        TeamMember member,
        GoogleSyncOutboxEvent? outboxEvent,
        CancellationToken ct)
    {
        if (outboxEvent is null)
            return await repo.ApproveRequestWithMemberAsync(request, member, ct);

        using var scope = CreateTransactionScope();
        var success = await repo.ApproveRequestWithMemberAsync(request, member, ct);
        if (!success)
            return false;

        await GoogleSyncOutboxService.AddAsync(outboxEvent, ct);
        scope.Complete();
        return true;
    }

    private async Task<IReadOnlyList<TeamRoleAssignment>> MarkMemberLeftWithOutboxAsync(
        TeamMember member,
        Guid? mergeIntoMemberId,
        CancellationToken ct)
    {
        var outboxEvent = await BuildOutboxEventAsync(
            member.Id, member.TeamId, member.UserId, GoogleSyncOutboxEventTypes.RemoveUserFromTeamResources, ct);
        var leftAt = clock.GetCurrentInstant();
        if (outboxEvent is null)
            return await repo.MarkMemberLeftAsync(member.Id, leftAt, mergeIntoMemberId, ct);

        using var scope = CreateTransactionScope();
        var removedAssignments = await repo.MarkMemberLeftAsync(member.Id, leftAt, mergeIntoMemberId, ct);
        await GoogleSyncOutboxService.AddAsync(outboxEvent, ct);
        scope.Complete();
        return removedAssignments;
    }

    private async Task<(TeamRoleAssignment Assignment, bool AutoAddedToTeam, TeamMember Member)> AssignToRoleWithOutboxAsync(
        Guid roleDefinitionId,
        Guid targetUserId,
        Guid actorUserId,
        TeamMember? autoAddMember,
        GoogleSyncOutboxEvent? outboxEvent,
        bool promoteToCoordinator,
        Instant now,
        CancellationToken ct)
    {
        if (outboxEvent is null)
        {
            return await repo.AssignToRoleAsync(
                roleDefinitionId,
                targetUserId,
                actorUserId,
                autoAddMember,
                promoteToCoordinator,
                now,
                ct);
        }

        using var scope = CreateTransactionScope();
        var result = await repo.AssignToRoleAsync(
            roleDefinitionId,
            targetUserId,
            actorUserId,
            autoAddMember,
            promoteToCoordinator,
            now,
            ct);

        if (result.AutoAddedToTeam)
            await GoogleSyncOutboxService.AddAsync(outboxEvent, ct);

        scope.Complete();
        return result;
    }

    private static TransactionScope CreateTransactionScope() =>
        new(
            TransactionScopeOption.Required,
            new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
            TransactionScopeAsyncFlowOption.Enabled);

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

        var removedAssignments = await MarkMemberLeftWithOutboxAsync(member, null, cancellationToken);

        await auditLogService.LogAsync(
            AuditAction.TeamLeft, nameof(Team), teamId,
            $"Left {team.Name}",
            userId,
            relatedEntityId: userId, relatedEntityType: nameof(User));

        InvalidateShiftAuthorizationIfNeeded(userId, removedAssignments);

        if (wasCoordinator)
        {
            await SystemTeamSync.SyncMembershipForUserAsync(
                userId, SystemTeamType.Coordinators, cancellationToken);
        }

        return new TeamMembershipMutationResult(true, WasCoordinator: wasCoordinator);
    }

    public async Task<TeamMembershipMutationResult> WithdrawJoinRequestAsync(
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetCurrentInstant();
        var withdrew = await repo.WithdrawRequestAsync(requestId, userId, now, cancellationToken);
        if (!withdrew)
            return new TeamMembershipMutationResult(false, "Teams_RequestUnavailable");

        notificationMeterInvalidator.Invalidate();

        logger.LogInformation("User {UserId} withdrew join request {RequestId}", userId, requestId);
        return new TeamMembershipMutationResult(true);
    }

    public async Task<TeamMember> ApproveJoinRequestAsync(
        Guid requestId,
        Guid approverUserId,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        var request = await repo.FindRequestWithTeamForMutationAsync(requestId, cancellationToken)
            ?? throw new InvalidOperationException("Join request not found");

        var canApprove = await CanUserApproveRequestsForTeamAsync(request.TeamId, approverUserId, cancellationToken);
        if (!canApprove)
            throw new InvalidOperationException("User does not have permission to approve requests for this team");

        if (request.Status != TeamJoinRequestStatus.Pending)
            throw new InvalidOperationException("Can only approve pending requests");
        request.Status = TeamJoinRequestStatus.Approved;
        request.ReviewedByUserId = approverUserId;
        request.ReviewNotes = notes;
        var now = clock.GetCurrentInstant();
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
            success = await ApproveRequestWithMemberAndOutboxAsync(
                request, member, outboxEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to approve join request {RequestId} for user {UserId} to team {TeamId}",
                requestId, request.UserId, request.TeamId);
            throw;
        }

        if (!success)
            throw new InvalidOperationException("User is already a member of this team");

        await auditLogService.LogAsync(
            AuditAction.TeamJoinRequestApproved, nameof(Team), request.TeamId,
            $"Join request for {request.Team.Name} approved",
            approverUserId,
            relatedEntityId: request.UserId, relatedEntityType: nameof(User));

        notificationMeterInvalidator.Invalidate();

        await SendAddedToTeamEmailAsync(request.UserId, request.Team, cancellationToken);
        await SendJoinRequestApprovedNotificationAsync(request.Team, member.UserId, cancellationToken);

        return member;
    }

    private async Task SendJoinRequestApprovedNotificationAsync(
        Team team,
        Guid requesterUserId,
        CancellationToken cancellationToken)
    {
        try
        {
            var culture = await GetRecipientCultureAsync(requesterUserId, cancellationToken);
            var title = string.Format(culture, NoticeResources.GetString("Teams_Notification_JoinApproved", culture)!, team.Name);
            await notificationService.SendAsync(
                NotificationSource.TeamJoinRequestDecided,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                title,
                [requesterUserId],
                body: string.Format(culture, NoticeResources.GetString("Teams_Notification_Welcome", culture)!, team.Name),
                actionUrl: $"/Teams/{team.Slug}",
                actionLabel: NoticeResources.GetString("Teams_Notification_ViewTeam", culture),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch TeamJoinRequestDecided notification for team {TeamId}", team.Id);
        }
    }

    public async Task RejectJoinRequestAsync(
        Guid requestId,
        Guid approverUserId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var request = await repo.FindRequestWithTeamForMutationAsync(requestId, cancellationToken)
            ?? throw new InvalidOperationException("Join request not found");

        var canApprove = await CanUserApproveRequestsForTeamAsync(request.TeamId, approverUserId, cancellationToken);
        if (!canApprove)
            throw new InvalidOperationException("User does not have permission to reject requests for this team");

        var now = clock.GetCurrentInstant();
        var rejected = await repo.RejectRequestAsync(requestId, approverUserId, reason, now, cancellationToken);
        if (!rejected)
            throw new InvalidOperationException("Can only reject pending requests");

        await auditLogService.LogAsync(
            AuditAction.TeamJoinRequestRejected, nameof(Team), request.TeamId,
            $"Join request for team rejected: {reason}",
            approverUserId,
            relatedEntityId: request.UserId, relatedEntityType: nameof(User));

        notificationMeterInvalidator.Invalidate();
        await SendJoinRequestRejectedNotificationAsync(request.Team, request.UserId, cancellationToken);
    }

    private async Task SendJoinRequestRejectedNotificationAsync(
        Team team,
        Guid requesterUserId,
        CancellationToken cancellationToken)
    {
        try
        {
            var culture = await GetRecipientCultureAsync(requesterUserId, cancellationToken);
            var title = string.Format(culture, NoticeResources.GetString("Teams_Notification_JoinRejected", culture)!, team.Name);
            await notificationService.SendAsync(
                NotificationSource.TeamJoinRequestDecided,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                title,
                [requesterUserId],
                body: string.Format(culture, NoticeResources.GetString("Teams_Notification_JoinRejected", culture)!, team.Name) + ".",
                actionUrl: "/Teams",
                actionLabel: NoticeResources.GetString("Teams_MyTeams_BrowseTeams", culture),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch TeamJoinRequestDecided notification for team {TeamId}", team.Id);
        }
    }

    public async Task<IReadOnlyList<TeamJoinRequestSnapshot>> GetPendingRequestsForTeamAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        var requests = await repo.GetPendingForTeamAsync(teamId, cancellationToken);
        var usersById = requests.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await UserService.GetUserInfosAsync(
                requests.Select(request => request.UserId)
                    .Distinct()
                    .ToList(),
                cancellationToken);
        return requests
            .Select(request => ToJoinRequestSnapshot(request, usersById))
            .ToList();
    }

    public async Task<IReadOnlyList<TeamJoinRequestSnapshot>> GetPendingRequestsForUserAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var requests = await repo.GetAllJoinRequestsForUserAsync(userId, cancellationToken);
        return requests.Where(request => request.Status == TeamJoinRequestStatus.Pending)
            .Select(ToJoinRequestSnapshot).ToList();
    }

    public async Task<TeamJoinRequestSnapshot?> GetUserPendingRequestAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var request = await repo.FindUserPendingRequestAsync(teamId, userId, cancellationToken);
        return request is null ? null : ToJoinRequestSnapshot(request);
    }

    private async Task<bool> CanUserApproveRequestsForTeamAsync(
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

    public async Task<bool> IsUserCoordinatorOfTeamAsync(
        Guid teamId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var teamsById = await LoadTeamsByIdAsync(cancellationToken);
        return TeamCoordinatorAccess.IsCoordinatorOfActiveTeam(teamsById, teamId, userId);
    }

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

        var canApprove = await CanUserApproveRequestsForTeamAsync(teamId, actorUserId, cancellationToken);
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
            mergeTarget = await repo.FindActiveMemberForMutationAsync(teamId, targetUserId, cancellationToken)
                ?? await AddMemberToTeamAsync(teamId, targetUserId, actorUserId, cancellationToken);
        }
        var wasCoordinator = member.Role == TeamMemberRole.Coordinator;

        var removedAssignments = await MarkMemberLeftWithOutboxAsync(member, mergeTarget?.Id, cancellationToken);

        await auditLogService.LogAsync(
            AuditAction.TeamMemberRemoved, nameof(Team), teamId,
            $"Member removed from {team.Name}",
            actorUserId,
            relatedEntityId: userId, relatedEntityType: nameof(User));

        InvalidateShiftAuthorizationIfNeeded(userId, removedAssignments);
        if (mergeTarget is not null)
        {
            InvalidateShiftAuthorizationIfNeeded(mergeTarget.UserId, removedAssignments);
            if (wasCoordinator || removedAssignments.Count > 0)
                await auditLogService.LogAsync(
                    AuditAction.TeamMemberRoleChanged, nameof(Team), teamId,
                    $"Folded {removedAssignments.Count} role assignment(s) from merged account {userId}; source coordinator: {wasCoordinator}",
                    actorUserId, mergeTarget.UserId, nameof(User));
        }

        try
        {
            var culture = await GetRecipientCultureAsync(userId, cancellationToken);
            var title = string.Format(culture, NoticeResources.GetString("Teams_Notification_MemberRemoved", culture)!, team.Name);
            await notificationService.SendAsync(
                NotificationSource.TeamMemberRemoved,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                title,
                [userId],
                body: null,
                actionUrl: "/Teams",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch TeamMemberRemoved notification for user {UserId} team {TeamId}", userId, teamId);
        }

        // Removing a coordinator changes their Coordinators system-team eligibility —
        // reconcile as part of the mutation, not as a caller-remembered follow-up.
        if (wasCoordinator && mergeTarget is null)
            await SystemTeamSync.SyncMembershipForUserAsync(userId, SystemTeamType.Coordinators, cancellationToken);
        return new TeamMemberRemovalResult();
    }

    private TeamMemberRemovalResult RefuseMemberRemoval(Guid teamId, Guid userId, Guid actorId, string key, string message)
    {
        logger.LogWarning("Member removal refused for {UserId} from team {TeamId} by {ActorId}: {ErrorKey}", userId, teamId, actorId, key);
        return new TeamMemberRemovalResult(key, message);
    }

    public async Task<TeamMember> AddMemberToTeamAsync(
        Guid teamId,
        Guid targetUserId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdAsync(teamId, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
            throw new InvalidOperationException("Cannot add members to system teams manually");

        targetUserId = await ResolveMembershipTargetAsync(targetUserId, cancellationToken);
        var isExisting = await repo.IsActiveMemberAsync(teamId, targetUserId, cancellationToken);
        if (isExisting)
            throw new InvalidOperationException("User is already a member of this team");

        // Resolve any pending request via approve-with-member; otherwise AddMemberWithOutbox.
        var pendingRequest = await repo.FindUserPendingRequestAsync(teamId, targetUserId, cancellationToken);
        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = targetUserId,
            Role = TeamMemberRole.Member,
            JoinedAt = clock.GetCurrentInstant()
        };

        var outboxEvent = await BuildOutboxEventAsync(
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
                    success = await ApproveRequestWithMemberAndOutboxAsync(
                        tracked, member, outboxEvent, cancellationToken);
                }
                else
                {
                    success = await TryAddMemberWithOutboxAsync(member, outboxEvent, cancellationToken);
                }
            }
            else
            {
                success = await TryAddMemberWithOutboxAsync(member, outboxEvent, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add user {UserId} to team {TeamId}", targetUserId, teamId);
            throw;
        }

        if (!success)
            throw new InvalidOperationException("User is already a member of this team");

        await auditLogService.LogAsync(
            AuditAction.TeamMemberAdded, nameof(Team), teamId,
            $"Member added to {team.Name}",
            actorUserId,
            relatedEntityId: targetUserId, relatedEntityType: nameof(User));

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
        var newRole = await repo.SetMemberRoleAsync(teamId, userId, role, cancellationToken);
        if (newRole is null)
            return;

        await auditLogService.LogAsync(
            AuditAction.TeamMemberRoleChanged, nameof(Team), teamId,
            $"Set member role to {role}",
            actorUserId,
            relatedEntityId: userId, relatedEntityType: nameof(User));
    }

    public async Task<TeamRoleDefinition> CreateRoleDefinitionAsync(
        Guid teamId, string name, string? description, int slotCount,
        List<SlotPriority> priorities, int sortOrder, RolePeriod period, Guid actorUserId,
        bool isPublic = true,
        int? estimatedHours = null,
        CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdAsync(teamId, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        ValidateSlotCountAndPriorities(slotCount, priorities);
        ValidateRoleName(name);

        var lowerName = name.ToLowerInvariant();
        var nameExists = await repo.RoleDefinitionNameExistsAsync(teamId, lowerName, excludingId: null, cancellationToken);
        if (nameExists)
            throw new InvalidOperationException($"A role definition with name '{name}' already exists for this team");

        var canManage = await CanUserApproveRequestsForTeamAsync(teamId, actorUserId, cancellationToken);
        if (!canManage)
            throw new InvalidOperationException("User does not have permission to manage role definitions for this team");

        var now = clock.GetCurrentInstant();
        var definition = new TeamRoleDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Name = name,
            Description = description,
            SlotCount = slotCount,
            EstimatedHours = estimatedHours,
            Priorities = priorities,
            SortOrder = sortOrder,
            IsPublic = isPublic,
            Period = period,
            CreatedAt = now,
            UpdatedAt = now
        };

        await repo.AddRoleDefinitionAsync(definition, cancellationToken);

        await auditLogService.LogAsync(
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
        bool canToggleManagement = true,
        int? estimatedHours = null,
        CancellationToken cancellationToken = default)
    {
        var definition = await repo.FindRoleDefinitionForMutationAsync(roleDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException($"Role definition {roleDefinitionId} not found");
        var team = await GetRoleDefinitionTeamAsync(definition, cancellationToken);

        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            throw new InvalidOperationException("User does not have permission to manage role definitions for this team");

        ValidateSlotCountAndPriorities(slotCount, priorities);

        if (!canToggleManagement)
            isManagement = definition.IsManagement;

        if (slotCount < definition.Assignments.Count)
            throw new InvalidOperationException(
                $"Cannot reduce slot count to {slotCount} — {definition.Assignments.Count} slots are currently filled");

        if (!string.Equals(definition.Name, name, StringComparison.Ordinal))
        {
            ValidateRoleName(name);
            var lowerName = name.ToLowerInvariant();
            var nameExists = await repo.RoleDefinitionNameExistsAsync(definition.TeamId, lowerName, excludingId: roleDefinitionId, cancellationToken);
            if (nameExists)
                throw new InvalidOperationException($"A role definition with name '{name}' already exists for this team");
        }

        definition.Name = name;
        definition.Description = description;
        definition.SlotCount = slotCount;
        definition.EstimatedHours = estimatedHours;
        definition.Priorities = priorities;
        definition.SortOrder = sortOrder;

        var usersNeedingShiftAuthorizationInvalidation =
            team.SystemTeamType == SystemTeamType.None &&
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
            var existingManagement = await repo.OtherRoleHasIsManagementAsync(
                definition.TeamId, roleDefinitionId, cancellationToken);
            if (existingManagement)
                throw new InvalidOperationException("Another role in this team is already marked as the management role");
        }

        var clearingIsManagement = definition.IsManagement && !isManagement;

        definition.IsPublic = isPublic;
        definition.IsManagement = isManagement;
        definition.Period = period;
        definition.UpdatedAt = clock.GetCurrentInstant();

        await repo.PersistRoleDefinitionUpdateAsync(
            definition, clearingIsManagement, cancellationToken);

        await auditLogService.LogAsync(
            AuditAction.TeamRoleDefinitionUpdated, nameof(TeamRoleDefinition), definition.Id,
            $"Role definition '{name}' updated for team {team.Name}",
            actorUserId,
            relatedEntityId: definition.TeamId, relatedEntityType: nameof(Team));

        InvalidateShiftAuthorization(usersNeedingShiftAuthorizationInvalidation);

        return definition;
    }

    public async Task DeleteRoleDefinitionAsync(
        Guid roleDefinitionId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var definition = await repo.FindRoleDefinitionForMutationAsync(roleDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException($"Role definition {roleDefinitionId} not found");
        var team = await GetRoleDefinitionTeamAsync(definition, cancellationToken);

        if (definition.IsManagement && definition.Assignments.Count > 0)
            throw new InvalidOperationException("Cannot delete the management role while members are assigned to it. Unassign all members first.");

        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            throw new InvalidOperationException("User does not have permission to manage role definitions for this team");

        await repo.RemoveRoleDefinitionAsync(definition, cancellationToken);

        await auditLogService.LogAsync(
            AuditAction.TeamRoleDefinitionDeleted, nameof(TeamRoleDefinition), definition.Id,
            $"Role definition '{definition.Name}' deleted from team {team.Name}",
            actorUserId,
            relatedEntityId: definition.TeamId, relatedEntityType: nameof(Team));
    }

    public async Task<TeamRoleManagementToggleResult> ToggleRoleIsManagementAsync(
        Guid roleDefinitionId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var definition = await repo.FindRoleDefinitionWithMembersForMutationAsync(roleDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException($"Role definition {roleDefinitionId} not found");
        var team = await GetRoleDefinitionTeamAsync(definition, cancellationToken);

        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            throw new InvalidOperationException("User does not have permission to manage role definitions for this team");

        var isManagement = !definition.IsManagement;
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

            var existingManagement = await repo.OtherRoleHasIsManagementAsync(
                definition.TeamId, roleDefinitionId, cancellationToken);
            if (existingManagement)
                throw new InvalidOperationException("Another role in this team is already marked as the management role");
        }

        var clearingIsManagement = definition.IsManagement && !isManagement && definition.Assignments.Count > 0;

        definition.IsManagement = isManagement;
        definition.UpdatedAt = clock.GetCurrentInstant();

        await repo.PersistRoleIsManagementAsync(
            definition, clearingIsManagement, cancellationToken);

        await auditLogService.LogAsync(
            AuditAction.TeamRoleDefinitionUpdated, nameof(TeamRoleDefinition), definition.Id,
            $"IsManagement set to {isManagement} on role '{definition.Name}' in {team.Name}",
            actorUserId,
            relatedEntityId: definition.TeamId, relatedEntityType: nameof(Team));

        InvalidateShiftAuthorization(usersNeedingShiftAuthorizationInvalidation);

        return new TeamRoleManagementToggleResult(definition.Name, definition.IsManagement);
    }

    public async Task<IReadOnlyList<TeamRoleDefinitionSnapshot>> GetRoleDefinitionsAsync(
        Guid teamId, CancellationToken cancellationToken = default)
    {
        var definitions = await repo.GetRoleDefinitionsAsync(teamId, cancellationToken);
        return definitions.Select(definition => ToRoleDefinitionSnapshot(definition)).ToList();
    }

    private static TeamRoleDefinitionSnapshot ToRoleDefinitionSnapshot(
        TeamRoleDefinition definition,
        Team? fallbackTeam = null) =>
        new(
            definition.Id,
            definition.TeamId,
            definition.Team?.Name ?? fallbackTeam?.Name ?? string.Empty,
            definition.Team?.Slug ?? fallbackTeam?.Slug ?? string.Empty,
            definition.Name,
            definition.Description,
            definition.SlotCount,
            definition.EstimatedHours,
            definition.Priorities,
            definition.SortOrder,
            definition.IsManagement,
            definition.Period,
            definition.IsPublic,
            definition.Assignments
                .Select(assignment => new TeamRoleAssignmentSnapshot(
                    assignment.Id,
                    assignment.TeamMemberId,
                    assignment.SlotIndex,
                    assignment.TeamMember?.UserId))
                .ToList());

    public async Task<TeamRoleAssignment> AssignToRoleAsync(
        Guid roleDefinitionId, Guid targetUserId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var definition = await repo.FindRoleDefinitionForMutationAsync(roleDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException($"Role definition {roleDefinitionId} not found");
        var team = await GetRoleDefinitionTeamAsync(definition, cancellationToken);

        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            throw new InvalidOperationException("User does not have permission to manage role assignments for this team");

        targetUserId = await ResolveMembershipTargetAsync(targetUserId, cancellationToken);
        var existingMember = await repo.FindActiveMemberForMutationAsync(definition.TeamId, targetUserId, cancellationToken);
        var now = clock.GetCurrentInstant();

        TeamMember? autoAddMember = null;
        GoogleSyncOutboxEvent? outboxEvent = null;
        if (existingMember is null)
        {
            if (team.IsSystemTeam)
                throw new InvalidOperationException(
                    "Cannot add members to system teams via role assignment; only existing members can be assigned to roles on system teams.");

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

        var (assignment, autoAddedToTeam, _) = await AssignToRoleWithOutboxAsync(
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
            await auditLogService.LogAsync(
                AuditAction.TeamMemberAdded, nameof(Team), definition.TeamId,
                $"Auto-added to {team.Name} via role assignment",
                actorUserId,
                relatedEntityId: targetUserId, relatedEntityType: nameof(User));
        }

        await auditLogService.LogAsync(
            AuditAction.TeamRoleAssigned, nameof(TeamRoleDefinition), roleDefinitionId,
            $"Assigned to role '{definition.Name}' in {team.Name}",
            actorUserId,
            relatedEntityId: targetUserId, relatedEntityType: nameof(User));

        InvalidateShiftAuthorizationIfNeeded(definition, targetUserId);

        // Role assignment can change Coordinators system-team eligibility (management
        // roles promote) — reconcile as part of the mutation.
        await SystemTeamSync.SyncMembershipForUserAsync(targetUserId, SystemTeamType.Coordinators, cancellationToken);

        return assignment;
    }

    public async Task UnassignFromRoleAsync(
        Guid roleDefinitionId, Guid teamMemberId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var definition = await repo.FindRoleDefinitionForMutationAsync(roleDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException($"Role definition {roleDefinitionId} not found");
        var team = await GetRoleDefinitionTeamAsync(definition, cancellationToken);

        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            throw new InvalidOperationException("User does not have permission to manage role assignments for this team");

        var (_, targetUserId) = await repo.UnassignFromRoleAsync(
            roleDefinitionId, teamMemberId, cancellationToken);

        await auditLogService.LogAsync(
            AuditAction.TeamRoleUnassigned, nameof(TeamRoleDefinition), roleDefinitionId,
            $"Unassigned from role '{definition.Name}' in {team.Name}",
            actorUserId,
            relatedEntityId: targetUserId, relatedEntityType: nameof(User));

        InvalidateShiftAuthorizationIfNeeded(definition, targetUserId);

        // Losing a (possibly management) role can change Coordinators system-team
        // eligibility — reconcile as part of the mutation.
        await SystemTeamSync.SyncMembershipForUserAsync(targetUserId, SystemTeamType.Coordinators, cancellationToken);
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

    // ==========================================================================
    // ITeamSeeding — the dev/demo fixture surface. Explicit implementations: the members of
    // the same names on this class return the section's entities, which cannot leave the
    // assembly, so these overloads differ only in their projection (design §15 step 5b).
    // ==========================================================================

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

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamsWithParentsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken = default)
    {
        // The read-model map is built from every team row, deactivated ones included, which is
        // what the entity-returning sibling promises its Teams-internal callers.
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

        // GoogleResource → Team is OnDelete(Restrict); pre-check yields a clear message vs raw FK violation.
        var resources = await TeamResourceService.GetTeamResourcesAsync(teamId, cancellationToken);
        if (resources.Count > 0)
            throw new InvalidOperationException("Cannot permanently delete a team that has Google resources linked. Unlink resources first.");

        return await repo.PermanentlyDeleteTeamAsync(teamId, cancellationToken);
    }

    public Task<IReadOnlyDictionary<Guid, string>> GetManagementRoleNamesByTeamIdsAsync(
        IEnumerable<Guid> teamIds,
        CancellationToken cancellationToken = default) =>
        repo.GetPublicManagementRoleNamesByTeamIdsAsync(teamIds.ToList(), cancellationToken);

    public async Task<AdminTeamListResult> GetAdminTeamListAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var (items, totalCount) = await repo.GetAllForAdminAsync(page, pageSize, cancellationToken);

        var pendingShiftCounts = await shiftManagementService
            .GetActivePendingShiftSignupCountsByTeamAsync(cancellationToken);

        var teamIds = items.Select(t => t.Id).ToList();
        var resourceSummaries = await TeamResourceService
            .GetTeamResourceSummariesAsync(teamIds, cancellationToken);

        return new AdminTeamListResult(
            BuildAdminTeamSummaries(items, pendingShiftCounts, resourceSummaries),
            totalCount);
    }

    // Cache mutation/invalidation are owned by CachingTeamService.
    public void RemoveMemberFromAllTeamsCache(Guid userId) { }

    public void InvalidateActiveTeamsCache() { }

    public async Task<int> RevokeAllMembershipsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetCurrentInstant();
        var count = await repo.RevokeAllMembershipsAsync(userId, now, cancellationToken);
        if (count > 0)
            RemoveMemberFromAllTeamsCache(userId);
        return count;
    }

    public async Task ReassignAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Guid actorUserId,
        Instant updatedAt,
        CancellationToken cancellationToken)
    {
        // End source memberships through the ordinary authorized removal/outbox path,
        // folding roles into the survivor in the same save. System teams reconcile separately.
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

        // TeamJoinRequest fold.
        await repo.ReassignActiveJoinRequestsAsync(sourceUserId, targetUserId, cancellationToken);
        notificationMeterInvalidator.Invalidate();

        // Early-entry grants fold.
        await repo.ReassignEarlyEntryGrantsAsync(sourceUserId, targetUserId, cancellationToken);
        // Always retry both reconciles, even when a previous pass already ended every
        // source membership before failing here.
        await SystemTeamSync.SyncMembershipForUserAsync(sourceUserId, SystemTeamType.Coordinators, cancellationToken);
        await SystemTeamSync.SyncMembershipForUserAsync(targetUserId, SystemTeamType.Coordinators, cancellationToken);
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var info = await UserService.GetUserInfoAsync(userId, ct);
        List<TeamMember> memberships = [];
        List<TeamJoinRequest> joinRequests = [];
        List<TeamEarlyEntryGrant> eeGrants = [];
        foreach (var id in info?.AllUserIds ?? [userId])
        {
            memberships.AddRange(await repo.GetAllMembershipsForUserAsync(id, ct));
            joinRequests.AddRange(await repo.GetAllJoinRequestsForUserAsync(id, ct));
            eeGrants.AddRange(await repo.GetEarlyEntryGrantsForUserAsync(id, ct));
        }
        var teamIds = memberships.Select(tm => tm.TeamId)
            .Concat(joinRequests.Select(tjr => tjr.TeamId))
            .Concat(eeGrants.Select(g => g.TeamId))
            .Distinct()
            .ToList();
        var teamsById = await repo.GetByIdsWithParentsAsync(teamIds, ct);

        string GetTeamName(Guid teamId) =>
            teamsById.TryGetValue(teamId, out var team) ? team.Name : string.Empty;

        var membershipSlice = new UserDataSlice(TeamMemberships, memberships.Select(tm => new
        {
            TeamName = GetTeamName(tm.TeamId),
            tm.Role,
            JoinedAt = tm.JoinedAt.ToIso8601(),
            LeftAt = tm.LeftAt.ToIso8601(),
            TeamRoles = tm.RoleAssignments.Select(tra => new
            {
                RoleName = tra.TeamRoleDefinition.Name,
                AssignedAt = tra.AssignedAt.ToIso8601()
            })
        }).ToList());

        var joinRequestSlice = new UserDataSlice(TeamJoinRequests, joinRequests.Select(tjr => new
        {
            TeamName = GetTeamName(tjr.TeamId),
            tjr.Status,
            tjr.Message,
            tjr.ReviewNotes,
            RequestedAt = tjr.RequestedAt.ToIso8601(),
            ResolvedAt = tjr.ResolvedAt.ToIso8601(),
            StateHistory = tjr.StateHistory.OrderBy(h => h.ChangedAt).Select(h => new
            {
                h.Status,
                ChangedAt = h.ChangedAt.ToIso8601(),
                h.Notes
            }).ToList()
        }).ToList());

        var earlyEntrySlice = new UserDataSlice(TeamEarlyEntry, eeGrants.Select(g => new
        {
            TeamName = GetTeamName(g.TeamId),
            g.ProjectName,
            EntryDate = g.EntryDate.ToInvariantDate(),
            GrantedAt = g.CreatedAt.ToIso8601(),
        }).ToList());

        return [membershipSlice, joinRequestSlice, earlyEntrySlice];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [TeamMemberships] =
                "Partially retained: the membership row (team, role, joined/left dates, keyed to " +
                "the user id the Users section tombstones) is the association's record of who " +
                "worked on what — Ley Orgánica 1/2002 Art. 14, GDPR Art. 17(3)(b). Every still-" +
                "active membership is ended immediately.",
            [TeamJoinRequests] = null,
            [TeamEarlyEntry] = null
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>
    /// Ends live memberships, then hard-deletes the join requests (they carry the
    /// applicant's free-text message) and the early-entry grants.
    /// </summary>
    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        await RevokeAllMembershipsAsync(userId, ct);
        await repo.DeleteJoinRequestsForUserAsync(userId, ct);
        notificationMeterInvalidator.Invalidate();
        await DeleteEarlyEntryGrantsForUserAsync(userId, ct);

        // No cache call here: this type is the inner service, where the invalidation
        // methods are empty bodies — the real ones live on CachingTeamService. The
        // deletion orchestrator drops both team caches through the decorator instead.
    }

    // ==========================================================================
    // Early-Entry Grants
    // ==========================================================================

    public async Task<IReadOnlyList<EarlyEntryGrant>> GetEarlyEntriesAsync(CancellationToken ct)
    {
        var grants = await repo.GetEarlyEntryGrantsForEnabledTeamsAsync(ct);
        return TeamEarlyEntryProjection.Project(grants);
    }

    public async Task<IReadOnlyList<TeamEarlyEntryGrantInfo>> GetEarlyEntryGrantsForTeamAsync(Guid teamId, CancellationToken ct = default)
    {
        var grants = await repo.GetEarlyEntryGrantsForTeamAsync(teamId, ct);
        return grants
            .Select(g => new TeamEarlyEntryGrantInfo(g.Id, g.UserId, g.EntryDate, g.ProjectName))
            .ToList();
    }

    public async Task AddEarlyEntryGrantAsync(Guid teamId, Guid userId, LocalDate entryDate, string projectName, Guid actorUserId, CancellationToken ct = default)
    {
        var team = await repo.GetByIdAsync(teamId, ct)
            ?? throw new InvalidOperationException($"Team {teamId} not found.");
        if (!team.EarlyEntryEnabled)
            throw new InvalidOperationException($"Early entry is not enabled for team {teamId}.");
        if (userId == Guid.Empty)
            throw new ArgumentException("UserId must be a non-empty GUID.", nameof(userId));

        var grant = new TeamEarlyEntryGrant
        {
            TeamId = teamId,
            UserId = userId,
            EntryDate = entryDate,
            ProjectName = projectName.Trim(),
            CreatedAt = clock.GetCurrentInstant(),
            CreatedByUserId = actorUserId,
        };
        await repo.AddEarlyEntryGrantAsync(grant, ct);
        await auditLogService.LogAsync(AuditAction.EarlyEntryGranted, nameof(TeamEarlyEntryGrant), grant.Id,
            $"EE granted to {userId} on {entryDate} for \"{grant.ProjectName}\" (team {teamId})", actorUserId);
    }

    public async Task EditEarlyEntryGrantAsync(Guid teamId, Guid grantId, LocalDate entryDate, string projectName, Guid actorUserId, CancellationToken ct = default)
    {
        var grant = await repo.FindEarlyEntryGrantForMutationAsync(grantId, ct);
        if (grant is null || grant.TeamId != teamId)
            throw new InvalidOperationException($"EE grant {grantId} not found.");
        grant.EntryDate = entryDate;
        grant.ProjectName = projectName.Trim();
        grant.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpdateEarlyEntryGrantAsync(grant, ct);
        await auditLogService.LogAsync(AuditAction.EarlyEntryUpdated, nameof(TeamEarlyEntryGrant), grant.Id,
            $"EE updated for {grant.UserId}: {entryDate}, \"{grant.ProjectName}\"", actorUserId);
    }

    public async Task RemoveEarlyEntryGrantAsync(Guid teamId, Guid grantId, Guid actorUserId, CancellationToken ct = default)
    {
        var grant = await repo.FindEarlyEntryGrantForMutationAsync(grantId, ct);
        if (grant is null || grant.TeamId != teamId) return; // idempotent / not found on this team
        await repo.RemoveEarlyEntryGrantAsync(grantId, ct);
        await auditLogService.LogAsync(AuditAction.EarlyEntryRevoked, nameof(TeamEarlyEntryGrant), grantId,
            $"EE revoked for {grant.UserId} (team {grant.TeamId})", actorUserId);
    }

    private async Task DeleteEarlyEntryGrantsForUserAsync(Guid userId, CancellationToken ct)
    {
        var grants = await repo.GetEarlyEntryGrantsForUserAsync(userId, ct);
        if (grants.Count == 0) return;
        await repo.RemoveEarlyEntryGrantsForUserAsync(userId, ct);
    }

    private async Task<IReadOnlyDictionary<Guid, TeamInfo>> LoadTeamsByIdAsync(CancellationToken ct = default)
    {
        var teams = await repo.GetAllWithMembersAsync(ct);
        var allUserIds = teams.SelectMany(t => t.Members.Where(m => m.LeftAt is null).Select(m => m.UserId))
            .Distinct()
            .ToList();
        var users = allUserIds.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await UserService.GetUserInfosAsync(allUserIds, ct);
        var managementHolders = await repo.GetActiveManagementRoleHolderUserIdsByTeamAsync(ct);
        var roleDefinitionsByTeam = await repo.GetAllRoleDefinitionsByTeamAsync(ct);
        var childIdsByParent = teams
            .Where(t => t.ParentTeamId.HasValue)
            .GroupBy(t => t.ParentTeamId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(t => t.Id).ToList());
        var pendingCounts = await repo.GetPendingCountsByTeamIdsAsync(
            teams.Select(t => t.Id).ToList(), ct);

        return teams.ToDictionary(
            t => t.Id,
            t => BuildTeamInfo(t, users, managementHolders, roleDefinitionsByTeam, childIdsByParent, pendingCounts));
    }

    private static TeamInfo BuildTeamInfo(
        Team team,
        IReadOnlyDictionary<Guid, UserInfo> users,
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
                    JoinedAt: m.JoinedAt);
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
            ? defs.Select(d => ToRoleDefinitionSnapshot(d, team)).ToList()
            : null,
        ChildTeamIds: childIdsByParent.TryGetValue(team.Id, out var childIds) ? childIds : null,
        ShowCoordinatorsOnPublicPage: team.ShowCoordinatorsOnPublicPage,
        PageContent: team.PageContent,
        CallsToAction: team.CallsToAction,
        PageContentUpdatedAt: team.PageContentUpdatedAt,
        PageContentUpdatedByUserId: team.PageContentUpdatedByUserId,
        PendingRequestCount: pendingCounts.TryGetValue(team.Id, out var pending) ? pending : 0,
        EarlyEntryEnabled: team.EarlyEntryEnabled);

    private void InvalidateShiftAuthorizationIfNeeded(Guid userId, IEnumerable<TeamRoleAssignment> roleAssignments)
    {
        if (roleAssignments.Any(assignment => IsShiftAuthorizationDefinition(assignment.TeamRoleDefinition)))
            shiftAuthInvalidator.Invalidate(userId);
    }

    private void InvalidateShiftAuthorizationIfNeeded(TeamRoleDefinition definition, Guid userId)
    {
        if (IsShiftAuthorizationDefinition(definition))
            shiftAuthInvalidator.Invalidate(userId);
    }

    private void InvalidateShiftAuthorization(IEnumerable<Guid> userIds)
    {
        foreach (var userId in userIds.Distinct())
            shiftAuthInvalidator.Invalidate(userId);
    }

    private async Task<Team> GetRoleDefinitionTeamAsync(TeamRoleDefinition definition, CancellationToken ct)
    {
        var teamsById = await repo.GetByIdsWithParentsAsync([definition.TeamId], ct);
        return teamsById.TryGetValue(definition.TeamId, out var team)
            ? team
            : throw new InvalidOperationException($"Team {definition.TeamId} not found");
    }

    private static bool IsShiftAuthorizationDefinition(TeamRoleDefinition definition) =>
        definition.IsManagement &&
        definition.Team.SystemTeamType == SystemTeamType.None;

    private async Task<Guid> ResolveMembershipTargetAsync(Guid userId, CancellationToken ct)
    {
        var user = await UserService.GetUserInfoAsync(userId, ct);
        if (user is null || user.IsTombstone)
            throw new InvalidOperationException("Cannot add a missing, deleted or merged account to a team.");
        return user.Id;
    }

    private async Task<GoogleSyncOutboxEvent?> BuildOutboxEventAsync(
        Guid teamMemberId,
        Guid teamId,
        Guid userId,
        string eventType,
        CancellationToken ct)
    {
        var identity = await GoogleIdentities.GetWorkspaceIdentityAsync(userId, ct);
        if (identity?.Status == GoogleEmailStatus.Rejected)
        {
            logger.LogDebug(
                "Skipping Google sync outbox event {EventType} for user {UserId} — Google identity is Rejected",
                eventType, userId);
            return null;
        }

        return new GoogleSyncOutboxEvent
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            TeamId = teamId,
            UserId = userId,
            OccurredAt = clock.GetCurrentInstant(),
            DeduplicationKey = $"{teamMemberId}:{eventType}"
        };
    }

    private async Task SendAddedToTeamEmailAsync(Guid userId, Team team, CancellationToken cancellationToken)
    {
        if (team.IsHidden) return;

        var culture = CultureInfo.GetCultureInfo("en");
        try
        {
            var users = await UserService.GetUserInfosAsync([userId], cancellationToken);
            if (!users.TryGetValue(userId, out var user))
                return;

            culture = CultureInfo.GetCultureInfo(user.PreferredLanguage.IsSupportedCultureCode() ? user.PreferredLanguage : "en");

            var email = user.Email;
            if (string.IsNullOrEmpty(email))
            {
                logger.LogWarning(
                    "Skipping added-to-team email for user {UserId}: no notification-target email",
                    userId);
            }
            else
            {
                var resources = await TeamResourceService.GetTeamResourcesAsync(team.Id, cancellationToken);

                await EmailService.SendAsync(EmailMessages.AddedToTeam(
                    email, user.BurnerName, team.Name, team.Slug,
                    resources.Select(r => (r.Name, r.Url)),
                    culture.Name),
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send added-to-team email for user {UserId} team {TeamId}", userId, team.Id);
        }

        try
        {
            var title = string.Format(culture, NoticeResources.GetString("Teams_Email_AddedToTeam_Subject", culture)!, team.Name);
            await notificationService.SendAsync(
                NotificationSource.TeamMemberAdded,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                title,
                [userId],
                body: null,
                actionUrl: $"/Teams/{team.Slug}",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch added-to-team inbox notification for user {UserId} team {TeamId}", userId, team.Id);
        }
    }

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

    private static TeamJoinRequestSnapshot ToJoinRequestSnapshot(TeamJoinRequest request) => new(
        request.Id,
        request.TeamId,
        request.Team?.Name,
        request.UserId,
        UserDisplayName: null,
        UserEmail: null,
        UserProfilePictureUrl: null,
        request.Status,
        request.Message,
        request.RequestedAt,
        request.ResolvedAt,
        request.ReviewNotes,
        request.Team?.Slug);

    private static TeamJoinRequestSnapshot ToJoinRequestSnapshot(
        TeamJoinRequest request,
        IReadOnlyDictionary<Guid, UserInfo> usersById)
    {
        usersById.TryGetValue(request.UserId, out var user);
        return new TeamJoinRequestSnapshot(
            request.Id,
            request.TeamId,
            request.Team?.Name,
            request.UserId,
            user?.BurnerName,
            user?.Email,
            user?.ProfilePictureUrl,
            request.Status,
            request.Message,
            request.RequestedAt,
            request.ResolvedAt,
            request.ReviewNotes);
    }

    private static void ValidateRoleName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Role name cannot be empty");

        if (name.Length > 100)
            throw new InvalidOperationException("Role name cannot exceed 100 characters");
    }

    private static void ValidateSlotCountAndPriorities(int slotCount, List<SlotPriority> priorities)
    {
        if (slotCount < 1)
            throw new InvalidOperationException("Slot count must be at least 1");

        if (priorities.Count != slotCount)
            throw new InvalidOperationException($"Priorities count ({priorities.Count}) must match slot count ({slotCount})");

        if (priorities.Any(priority => !Enum.IsDefined(priority)))
            throw new InvalidOperationException("Each slot priority must be a defined priority value");
    }

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

    public async Task<int> ApplyMemberRoleChangesAsync(
        IReadOnlyCollection<(Guid TeamMemberId, TeamMemberRole Role)> changes,
        CancellationToken cancellationToken = default)
    {
        if (changes.Count == 0)
            return 0;
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

    public async Task<Dictionary<string, Guid[]>> GetExpectedAsync(
        string? groupKey = null,
        CancellationToken ct = default)
    {
        var requestedKey = string.IsNullOrWhiteSpace(groupKey)
            ? null
            : groupKey.Trim();

        var teams = await repo.GetAllActiveAsync(ct);
        var groupTeams = teams
            .Where(t => t.GoogleGroupEmail is not null)
            .Where(t => requestedKey is null
                || string.Equals(t.GoogleGroupEmail, requestedKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (groupTeams.Count == 0)
            return new Dictionary<string, Guid[]>(StringComparer.OrdinalIgnoreCase);

        var duplicateGroupEmails = groupTeams
            .GroupBy(t => t.GoogleGroupEmail!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var duplicateGroupEmail in duplicateGroupEmails)
        {
            logger.LogWarning(
                "Multiple active Teams share Google group {GroupKey}; skipping this group membership claim so Google sync fails closed",
                duplicateGroupEmail);
        }

        var syncableGroupTeams = groupTeams
            .Where(t => !duplicateGroupEmails.Contains(t.GoogleGroupEmail!))
            .ToList();
        if (syncableGroupTeams.Count == 0)
            return new Dictionary<string, Guid[]>(StringComparer.OrdinalIgnoreCase);

        var groupTeamIds = syncableGroupTeams.Select(t => t.Id).ToHashSet();
        var parentIdByChildTeamId = teams
            .Where(t => t.ParentTeamId is { } parentId && groupTeamIds.Contains(parentId))
            .ToDictionary(t => t.Id, t => t.ParentTeamId!.Value);

        var childMemberUserIdsByParentTeam = teams
            .Where(t => parentIdByChildTeamId.ContainsKey(t.Id))
            .GroupBy(t => parentIdByChildTeamId[t.Id])
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(t => t.Members.Where(m => m.LeftAt is null).Select(m => m.UserId))
                    .Distinct()
                    .ToArray());

        var result = new Dictionary<string, Guid[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var team in syncableGroupTeams)
        {
            var directUserIds = team.Members
                .Where(m => m.LeftAt is null)
                .Select(m => m.UserId);

            var childUserIds = childMemberUserIdsByParentTeam.GetValueOrDefault(team.Id)
                ?? [];

            result[team.GoogleGroupEmail!] = directUserIds
                .Concat(childUserIds)
                .Distinct()
                .ToArray();
        }

        return result;
    }
}
