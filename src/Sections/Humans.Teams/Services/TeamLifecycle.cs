using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.Base.Enums;
using Humans.Base.Helpers;
using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Domain;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Teams.Services;

internal sealed class TeamLifecycle(
    ITeamRepository repo,
    IAdminAuthorizationService adminAuthorization,
    IAuditLogService auditLogService,
    IClock clock,
    ILogger<TeamService> logger,
    IServiceProvider serviceProvider) : IApplicationService
{
    private const int MaxTeamSlugLength = 256;
    private static readonly HashSet<string> ReservedTeamSlugs = new(StringComparer.Ordinal)
    {
        "roster", "birthdays", "map", "my", "sync", "summary", "create", "search", "tree"
    };

    // Lazy: UserService injects ITeamManagement, closing a cycle.
    private ITeamResourceService TeamResourceService
        => serviceProvider.GetRequiredService<ITeamResourceService>();

    private ISystemTeamSync SystemTeamSync
        => serviceProvider.GetRequiredService<ISystemTeamSync>();

    internal async Task<Team> CreateTeamAsync(
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

    internal async Task<TeamUpdateResult> CreateTeamCoreAsync(
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

    private async Task<string?> ValidateParentAsync(Guid parentTeamId, CancellationToken ct)
    {
        var parent = await repo.GetByIdAsync(parentTeamId, ct);
        if (parent is null) return $"Parent team {parentTeamId} not found";
        if (parent.IsSystemTeam) return "System teams cannot be parents";
        return parent.ParentTeamId.HasValue
            ? "Cannot nest more than one level — the parent team already has a parent"
            : null;
    }

    internal async Task<TeamUpdateResult> UpdateTeamAsync(
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

    internal async Task<TeamDeactivationResult> DeleteTeamAsync(Guid teamId, Guid actorUserId, CancellationToken cancellationToken = default)
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

    internal async Task<bool> PermanentlyDeleteTeamAsync(
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

}
