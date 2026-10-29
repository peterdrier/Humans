using Humans.Auth.Data;
using Humans.Auth.Contracts;
using NodaTime;
using Humans.Base.Attributes;
using Humans.Base.Extensions;
using Humans.Base.Constants;
using Humans.Auth.Domain;
using Humans.AuditLog.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;

using Humans.Teams.Contracts;

namespace Humans.Auth.Services;

[DontFix(
    reason: "Auth (crosscut) references vertical sections — IUserService for assignee/creator display stitching, ISystemTeamSync for system-team membership. Permanent exception pending Peter-led inversion.",
    since: "2026-05-25")]
internal sealed class RoleAssignmentService(
    IRoleAssignmentRepository repository,
    IUserService userService,
    IAuditLogService auditLogService,
    INotificationEmitter notificationService,
    ISystemTeamSync systemTeamSyncJob,
    CachingRoleAssignmentService rowCache,
    RoleCatalog catalog,
    IClock clock,
    ILogger<RoleAssignmentService> logger) : IRoleAssignmentManagement, IUserDataContributor, IUserMerge
{
    internal const string RoleAssignments = "RoleAssignments";

    public async Task<(IReadOnlyList<RoleAssignmentSummarySnapshot> Items, int TotalCount)> GetFilteredAsync(
        string? roleFilter, bool activeOnly, int page, int pageSize, Instant now,
        CancellationToken ct = default)
    {
        var (items, totalCount) = await repository.GetFilteredAsync(
            string.IsNullOrWhiteSpace(roleFilter) ? null : catalog.StoredNames(roleFilter), activeOnly, page, pageSize, now, ct);

        return (await ToSummarySnapshotsAsync(items, ct), totalCount);
    }

    public async Task<RoleAssignmentDetailSnapshot?> GetByIdAsync(Guid assignmentId, CancellationToken ct = default)
    {
        var assignment = await repository.GetByIdAsync(assignmentId, ct);
        if (assignment is null) return null;

        var user = await userService.GetUserInfoAsync(assignment.UserId, ct);
        return new RoleAssignmentDetailSnapshot(
            assignment.UserId,
            catalog.Canonical(assignment.RoleName),
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
            catalog.Canonical(assignment.RoleName),
            assignment.ValidFrom,
            assignment.ValidTo,
            assignment.Notes,
            assignment.CreatedByUserId,
            creator?.BurnerName,
            assignment.CreatedAt);
        }).ToList();
    }

    public async Task<RoleAssignmentResult> AssignRoleAsync(
        Guid userId, string roleName, Guid assignerId,
        string? notes,
        CancellationToken ct = default)
    {
        if (!catalog.Contains(roleName))
        {
            return new RoleAssignmentResult(false, "UnknownRole");
        }

        var now = clock.GetCurrentInstant();

        var hasOverlap = await repository.HasOverlappingAssignmentAsync(
            userId, catalog.StoredNames(roleName), now, validTo: null, ct);
        if (hasOverlap)
        {
            return new RoleAssignmentResult(false, "RoleAlreadyActive");
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
        // Evict before anything below reads roles back (the Board team sync).
        rowCache.Clear();

        await auditLogService.LogAsync(
            AuditAction.RoleAssigned, nameof(User), userId,
            $"'{roleName}'",
            assignerId);

        // Best-effort in-app notification.
        try
        {
            await notificationService.SendAsync(
                AuthNotificationSources.RoleAssignmentChanged,
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

        return new RoleAssignmentResult(true);
    }

    public async Task<RoleAssignmentResult> EndRoleAsync(
        Guid assignmentId, Guid enderId,
        string? notes,
        CancellationToken ct = default)
    {
        var roleAssignment = await repository.FindForMutationAsync(assignmentId, ct);

        if (roleAssignment is null)
        {
            return new RoleAssignmentResult(false, "NotFound");
        }

        var now = clock.GetCurrentInstant();

        if (!roleAssignment.IsActive(now))
        {
            return new RoleAssignmentResult(false, "RoleNotActive");
        }

        roleAssignment.ValidTo = now;
        if (!string.IsNullOrWhiteSpace(notes))
        {
            roleAssignment.Notes = string.IsNullOrEmpty(roleAssignment.Notes)
                ? $"Ended: {notes}"
                : $"{roleAssignment.Notes} | Ended: {notes}";
        }

        await repository.UpdateAsync(roleAssignment, ct);
        // Evict before anything below reads roles back (the Board team sync).
        rowCache.Clear();

        await auditLogService.LogAsync(
            AuditAction.RoleEnded, nameof(User), roleAssignment.UserId,
            $"'{roleAssignment.RoleName}'",
            enderId);

        // Best-effort in-app notification.
        try
        {
            await notificationService.SendAsync(
                AuthNotificationSources.RoleAssignmentChanged,
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

        if (string.Equals(catalog.Canonical(roleAssignment.RoleName), RoleNames.Board, StringComparison.Ordinal))
        {
            await systemTeamSyncJob.SyncBoardTeamAsync();
        }

        return new RoleAssignmentResult(true);
    }

    public Task<bool> IsUserAdminAsync(Guid userId, CancellationToken cancellationToken = default) =>
        HasActiveRoleAsync(userId, RoleNames.Admin, cancellationToken);

    public Task<bool> IsUserBoardMemberAsync(Guid userId, CancellationToken cancellationToken = default) =>
        HasActiveRoleAsync(userId, RoleNames.Board, cancellationToken);

    public Task<bool> IsUserTeamsAdminAsync(Guid userId, CancellationToken cancellationToken = default) =>
        HasActiveRoleAsync(userId, TeamsRoles.TeamsAdmin, cancellationToken);

    public Task<bool> HasActiveRoleAsync(Guid userId, string roleName, CancellationToken cancellationToken = default) =>
        repository.HasActiveRoleAsync(userId, catalog.StoredNames(roleName), clock.GetCurrentInstant(), cancellationToken);

    public Task<bool> HasAnyActiveAssignmentAsync(Guid userId, CancellationToken cancellationToken = default) =>
        repository.HasAnyActiveAssignmentAsync(userId, clock.GetCurrentInstant(), cancellationToken);

    public Task<IReadOnlyList<Guid>> GetUserIdsWithActiveAssignmentsAsync(CancellationToken cancellationToken = default) =>
        repository.GetUserIdsWithActiveAssignmentsAsync(clock.GetCurrentInstant(), cancellationToken);

    public Task<IReadOnlyList<Guid>> GetActiveUserIdsInRoleAsync(
        string roleName, CancellationToken ct = default) =>
        repository.GetActiveUserIdsInRoleAsync(catalog.StoredNames(roleName), clock.GetCurrentInstant(), ct);

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
        rowCache.Clear();

        return activeRoles.Count;
    }

    public async Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken cancellationToken)
    {
        // The merge commits each section's step on its own, so the re-FK is final when it returns. Evict even
        // when it throws.
        try
        {
            await repository.ReassignToUserAsync(sourceUserId, targetUserId, updatedAt, catalog.Canonical, cancellationToken);
        }
        finally
        {
            rowCache.Clear();
        }
    }

    public async Task<IReadOnlyList<RoleAssignmentSnapshot>> GetActiveForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var all = await repository.GetByUserIdAsync(userId, ct);
        return all
            .Where(ra => ra.IsActive(now))
            .Select(ra => new RoleAssignmentSnapshot(catalog.Canonical(ra.RoleName), ra.ValidTo))
            .OrderBy(ra => ra.RoleName, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, int>> GetActiveCountsByRoleAsync(CancellationToken ct = default)
    {
        var stored = await repository.GetActiveCountsByRoleAsync(clock.GetCurrentInstant(), ct);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, count) in stored)
        {
            var current = catalog.Canonical(name);
            counts[current] = counts.GetValueOrDefault(current) + count;
        }
        return counts;
    }

    /// <summary>Rows the <c>auth.role-renames</c> data migration still has to rewrite.</summary>
    internal async Task<IReadOnlyList<(Guid UserId, string StoredName, string CurrentName, Instant? ValidTo)>>
        GetRowsUnderFormerNamesAsync(CancellationToken ct)
    {
        var rows = await repository.GetByRoleNamesForMutationAsync(catalog.AllFormerNames, ct);
        return [.. rows.Select(r => (r.UserId, r.RoleName, catalog.Canonical(r.RoleName), r.ValidTo))];
    }

    /// <summary>Idempotent: a second run finds no row under a former name.</summary>
    internal async Task<int> RewriteFormerNamesAsync(CancellationToken ct)
    {
        var rows = await repository.GetByRoleNamesForMutationAsync(catalog.AllFormerNames, ct);
        foreach (var row in rows)
        {
            row.RoleName = catalog.Canonical(row.RoleName);
        }

        await repository.UpdateManyAsync(rows, ct);
        rowCache.Clear();
        return rows.Count;
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var assignments = await repository.GetByUserIdAsync(userId, ct);

        var shaped = assignments.Select(ra => new
        {
            RoleName = catalog.Canonical(ra.RoleName),
            ValidFrom = ra.ValidFrom.ToIso8601(),
            ValidTo = ra.ValidTo.ToIso8601()
        }).ToList();

        return [new UserDataSlice(RoleAssignments, shaped)];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [RoleAssignments] =
                "Partially retained: expired role rows (role name + validity dates, keyed to the " +
                "user id the Users section tombstones) stay as the association's record of who " +
                "held which governance office — Ley Orgánica 1/2002 Arts. 11 and 14, GDPR " +
                "Art. 17(3)(b). Every still-active assignment is ended immediately."
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        RevokeAllActiveAsync(userId, ct);
}
