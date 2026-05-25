using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Architecture;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Auth;

namespace Humans.Application.Services.Auth;

// Stitches display data from UserInfo in memory — design-rules §6b in-memory join.
[DontFix(
    reason: "Auth (crosscut) references vertical sections — IUserServiceRead for assignee/creator display stitching, ISystemTeamSync for system-team membership. Permanent exception pending Peter-led inversion.",
    since: "2026-05-25")]
public sealed class RoleAssignmentService(
    IRoleAssignmentRepository repository,
    IUserServiceRead userService,
    IAuditLogService auditLogService,
    INotificationEmitter notificationService,
    ISystemTeamSync systemTeamSyncJob,
    INavBadgeCacheInvalidator navBadge,
    IRoleAssignmentClaimsCacheInvalidator claimsInvalidator,
    IRoleAssignmentCacheInvalidator roleAssignmentCacheInvalidator,
    IClock clock,
    ILogger<RoleAssignmentService> logger) : IRoleAssignmentService, IUserDataContributor, IUserMerge
{
    public Task<bool> HasOverlappingAssignmentAsync(
        Guid userId,
        string roleName,
        Instant validFrom,
        Instant? validTo = null,
        CancellationToken cancellationToken = default) =>
        repository.HasOverlappingAssignmentAsync(userId, roleName, validFrom, validTo, cancellationToken);

    public async Task<(IReadOnlyList<RoleAssignmentSummarySnapshot> Items, int TotalCount)> GetFilteredAsync(
        string? roleFilter, bool activeOnly, int page, int pageSize, Instant now,
        CancellationToken ct = default)
    {
        var (items, totalCount) = await repository.GetFilteredAsync(
            roleFilter, activeOnly, page, pageSize, now, ct);

        return (await ToSummarySnapshotsAsync(items, ct), totalCount);
    }

    public async Task<RoleAssignmentDetailSnapshot?> GetByIdAsync(Guid assignmentId, CancellationToken ct = default)
    {
        var assignment = await repository.GetByIdAsync(assignmentId, ct);
        if (assignment is null) return null;

        var user = await userService.GetUserInfoAsync(assignment.UserId, ct);
        return new RoleAssignmentDetailSnapshot(
            assignment.UserId,
            assignment.RoleName,
            user?.BurnerName ?? "Unknown");
    }

    public async Task<IReadOnlyList<RoleAssignmentSummarySnapshot>> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        var items = await repository.GetByUserIdAsync(userId, ct);
        return await ToSummarySnapshotsAsync(items, ct);
    }

    private async Task<IReadOnlyList<RoleAssignmentSummarySnapshot>> ToSummarySnapshotsAsync(
        IReadOnlyList<RoleAssignment> assignments,
        CancellationToken ct)
    {
        var userIds = assignments
            .Select(a => a.UserId)
            .Concat(assignments.Select(a => a.CreatedByUserId))
            .Distinct()
            .ToList();
        var users = userIds.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await userService.GetUserInfosAsync(userIds, ct);

        return assignments.Select(assignment =>
        {
            users.TryGetValue(assignment.UserId, out var user);
            users.TryGetValue(assignment.CreatedByUserId, out var creator);
            return new RoleAssignmentSummarySnapshot(
            assignment.Id,
            assignment.UserId,
            user?.Email,
            user?.BurnerName ?? "Unknown",
            assignment.RoleName,
            assignment.ValidFrom,
            assignment.ValidTo,
            assignment.Notes,
            assignment.CreatedByUserId,
            creator?.BurnerName,
            assignment.CreatedAt);
        }).ToList();
    }

    public async Task<OnboardingResult> AssignRoleAsync(
        Guid userId, string roleName, Guid assignerId,
        string? notes,
        CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();

        var hasOverlap = await repository.HasOverlappingAssignmentAsync(userId, roleName, now, validTo: null, ct);
        if (hasOverlap)
        {
            return new OnboardingResult(false, "RoleAlreadyActive");
        }

        var roleAssignment = new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleName = roleName,
            ValidFrom = now,
            Notes = notes,
            CreatedAt = now,
            CreatedByUserId = assignerId
        };

        await repository.AddAsync(roleAssignment, ct);
        roleAssignmentCacheInvalidator.InvalidateAll();

        await auditLogService.LogAsync(
            AuditAction.RoleAssigned, nameof(User), userId,
            $"'{roleName}'",
            assignerId);

        navBadge.Invalidate();
        claimsInvalidator.Invalidate(userId);

        // Best-effort in-app notification.
        try
        {
            await notificationService.SendAsync(
                NotificationSource.RoleAssignmentChanged,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                $"You were assigned the {roleName} role",
                [userId],
                actionUrl: "/Profile",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch RoleAssignmentChanged notification for user {UserId} role {Role}", userId, roleName);
        }

        if (string.Equals(roleName, RoleNames.Board, StringComparison.Ordinal))
        {
            await systemTeamSyncJob.SyncBoardTeamAsync();
        }

        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> EndRoleAsync(
        Guid assignmentId, Guid enderId,
        string? notes,
        CancellationToken ct = default)
    {
        var roleAssignment = await repository.FindForMutationAsync(assignmentId, ct);

        if (roleAssignment is null)
        {
            return new OnboardingResult(false, "NotFound");
        }

        var now = clock.GetCurrentInstant();

        if (!roleAssignment.IsActive(now))
        {
            return new OnboardingResult(false, "RoleNotActive");
        }

        roleAssignment.ValidTo = now;
        if (!string.IsNullOrWhiteSpace(notes))
        {
            roleAssignment.Notes = string.IsNullOrEmpty(roleAssignment.Notes)
                ? $"Ended: {notes}"
                : $"{roleAssignment.Notes} | Ended: {notes}";
        }

        await repository.UpdateAsync(roleAssignment, ct);
        roleAssignmentCacheInvalidator.InvalidateAll();

        await auditLogService.LogAsync(
            AuditAction.RoleEnded, nameof(User), roleAssignment.UserId,
            $"'{roleAssignment.RoleName}'",
            enderId);

        navBadge.Invalidate();
        claimsInvalidator.Invalidate(roleAssignment.UserId);

        // Best-effort in-app notification.
        try
        {
            await notificationService.SendAsync(
                NotificationSource.RoleAssignmentChanged,
                NotificationClass.Informational,
                NotificationPriority.Normal,
                $"Your {roleAssignment.RoleName} role assignment has ended",
                [roleAssignment.UserId],
                actionUrl: "/Profile",
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispatch RoleAssignmentChanged notification for user {UserId} role {Role}", roleAssignment.UserId, roleAssignment.RoleName);
        }

        if (string.Equals(roleAssignment.RoleName, RoleNames.Board, StringComparison.Ordinal))
        {
            await systemTeamSyncJob.SyncBoardTeamAsync();
        }

        return new OnboardingResult(true);
    }

    public Task<bool> IsUserAdminAsync(Guid userId, CancellationToken cancellationToken = default) =>
        repository.HasActiveRoleAsync(userId, RoleNames.Admin, clock.GetCurrentInstant(), cancellationToken);

    public Task<bool> IsUserBoardMemberAsync(Guid userId, CancellationToken cancellationToken = default) =>
        repository.HasActiveRoleAsync(userId, RoleNames.Board, clock.GetCurrentInstant(), cancellationToken);

    public Task<bool> IsUserTeamsAdminAsync(Guid userId, CancellationToken cancellationToken = default) =>
        repository.HasActiveRoleAsync(userId, RoleNames.TeamsAdmin, clock.GetCurrentInstant(), cancellationToken);

    public Task<bool> HasActiveRoleAsync(Guid userId, string roleName, CancellationToken cancellationToken = default) =>
        repository.HasActiveRoleAsync(userId, roleName, clock.GetCurrentInstant(), cancellationToken);

    public Task<bool> HasAnyActiveAssignmentAsync(Guid userId, CancellationToken cancellationToken = default) =>
        repository.HasAnyActiveAssignmentAsync(userId, clock.GetCurrentInstant(), cancellationToken);

    public Task<IReadOnlyList<Guid>> GetUserIdsWithActiveAssignmentsAsync(CancellationToken cancellationToken = default) =>
        repository.GetUserIdsWithActiveAssignmentsAsync(clock.GetCurrentInstant(), cancellationToken);

    public Task<IReadOnlyList<Guid>> GetActiveUserIdsInRoleAsync(
        string roleName, CancellationToken ct = default) =>
        repository.GetActiveUserIdsInRoleAsync(roleName, clock.GetCurrentInstant(), ct);

    public async Task<int> RevokeAllActiveAsync(Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();

        var activeRoles = await repository.GetActiveForUserForMutationAsync(userId, now, ct);

        if (activeRoles.Count == 0)
            return 0;

        foreach (var role in activeRoles)
        {
            role.ValidTo = now;
        }

        await repository.UpdateManyAsync(activeRoles, ct);
        roleAssignmentCacheInvalidator.InvalidateAll();
        claimsInvalidator.Invalidate(userId);

        return activeRoles.Count;
    }

    public Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken cancellationToken)
    {
        // Caller invalidates caches AFTER the ambient TransactionScope commits — see AccountMergeService.AcceptAsync.
        return repository.ReassignToUserAsync(sourceUserId, targetUserId, updatedAt, cancellationToken);
    }

    public void InvalidateClaimsCacheForUser(Guid userId) => claimsInvalidator.Invalidate(userId);

    public void InvalidateNavBadgeCache() => navBadge.Invalidate();

    public void InvalidateRoleAssignmentCache() => roleAssignmentCacheInvalidator.InvalidateAll();

    public async Task<IReadOnlyList<RoleAssignmentSnapshot>> GetActiveForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var all = await repository.GetByUserIdAsync(userId, ct);
        return all
            .Where(ra => ra.ValidFrom <= now && (ra.ValidTo == null || ra.ValidTo > now))
            .OrderBy(ra => ra.RoleName, StringComparer.Ordinal)
            .Select(ra => new RoleAssignmentSnapshot(ra.RoleName, ra.ValidTo))
            .ToList();
    }

    public Task<IReadOnlyDictionary<string, int>> GetActiveCountsByRoleAsync(CancellationToken ct = default) =>
        repository.GetActiveCountsByRoleAsync(clock.GetCurrentInstant(), ct);

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var assignments = await repository.GetByUserIdAsync(userId, ct);

        var shaped = assignments.Select(ra => new
        {
            ra.RoleName,
            ValidFrom = ra.ValidFrom.ToInvariantInstantString(),
            ValidTo = ra.ValidTo.ToInvariantInstantString()
        }).ToList();

        return [new UserDataSlice(GdprExportSections.RoleAssignments, shaped)];
    }

}
