using Microsoft.Extensions.Logging;
using NodaTime;
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

/// <summary>
/// Application-layer implementation of <see cref="IRoleAssignmentService"/>.
/// Goes through <see cref="IRoleAssignmentRepository"/> for all data access —
/// this type never imports <c>Microsoft.EntityFrameworkCore</c>, enforced by
/// <c>Humans.Application.csproj</c>'s reference graph. Cross-section reads
/// (reporter / assigner display names) go through <see cref="IUserService"/>.
/// Cross-cutting cache invalidation is routed through
/// <see cref="INavBadgeCacheInvalidator"/> and
/// <see cref="IRoleAssignmentClaimsCacheInvalidator"/> instead of
/// <c>IMemoryCache</c> directly.
/// </summary>
/// <remarks>
/// Auth writes are rare (handful of admin events per month) and reads are
/// handful per day, so no caching decorator sits in front of this service —
/// same rationale as Governance / Feedback. The service stitches display
/// data in memory onto the <see cref="RoleAssignment"/> entity's (now
/// <c>[Obsolete]</c>) cross-domain navigation properties so existing
/// controllers and views can continue to read <c>ra.User.DisplayName</c>,
/// <c>ra.CreatedByUser.DisplayName</c>, etc. without change — this is the
/// "in-memory join" from design-rules §6b.
/// </remarks>
public sealed class RoleAssignmentService : IRoleAssignmentService, IUserDataContributor
{
    private readonly IRoleAssignmentRepository _repository;
    private readonly IUserService _userService;
    private readonly IAuditLogService _auditLogService;
    private readonly INotificationEmitter _notificationService;
    private readonly ISystemTeamSync _systemTeamSyncJob;
    private readonly INavBadgeCacheInvalidator _navBadge;
    private readonly IRoleAssignmentClaimsCacheInvalidator _claimsInvalidator;
    private readonly IClock _clock;
    private readonly ILogger<RoleAssignmentService> _logger;

    public RoleAssignmentService(
        IRoleAssignmentRepository repository,
        IUserService userService,
        IAuditLogService auditLogService,
        INotificationEmitter notificationService,
        ISystemTeamSync systemTeamSyncJob,
        INavBadgeCacheInvalidator navBadge,
        IRoleAssignmentClaimsCacheInvalidator claimsInvalidator,
        IClock clock,
        ILogger<RoleAssignmentService> logger)
    {
        _repository = repository;
        _userService = userService;
        _auditLogService = auditLogService;
        _notificationService = notificationService;
        _systemTeamSyncJob = systemTeamSyncJob;
        _navBadge = navBadge;
        _claimsInvalidator = claimsInvalidator;
        _clock = clock;
        _logger = logger;
    }

    public Task<bool> HasOverlappingAssignmentAsync(
        Guid userId,
        string roleName,
        Instant validFrom,
        Instant? validTo = null,
        CancellationToken cancellationToken = default) =>
        _repository.HasOverlappingAssignmentAsync(userId, roleName, validFrom, validTo, cancellationToken);

    public async Task<(IReadOnlyList<RoleAssignment> Items, int TotalCount)> GetFilteredAsync(
        string? roleFilter, bool activeOnly, int page, int pageSize, Instant now,
        CancellationToken ct = default)
    {
        var (items, totalCount) = await _repository.GetFilteredAsync(
            roleFilter, activeOnly, page, pageSize, now, ct);

        await StitchCrossDomainNavsAsync(items, ct);
        return (items, totalCount);
    }

    public async Task<RoleAssignment?> GetByIdAsync(Guid assignmentId, CancellationToken ct = default)
    {
        var assignment = await _repository.GetByIdAsync(assignmentId, ct);
        if (assignment is null) return null;

        await StitchCrossDomainNavsAsync([assignment], ct);
        return assignment;
    }

    public async Task<IReadOnlyList<RoleAssignment>> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        var items = await _repository.GetByUserIdAsync(userId, ct);
        await StitchCrossDomainNavsAsync(items, ct);
        return items;
    }

    public async Task<OnboardingResult> AssignRoleAsync(
        Guid userId, string roleName, Guid assignerId,
        string? notes,
        CancellationToken ct = default)
    {
        var now = _clock.GetCurrentInstant();

        var hasOverlap = await _repository.HasOverlappingAssignmentAsync(userId, roleName, now, validTo: null, ct);
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

        await _repository.AddAsync(roleAssignment, ct);

        await _auditLogService.LogAsync(
            AuditAction.RoleAssigned, nameof(User), userId,
            $"Role '{roleName}' assigned",
            assignerId);

        _navBadge.Invalidate();
        _claimsInvalidator.Invalidate(userId);

        // In-app notification to the user (best-effort)
        try
        {
            await _notificationService.SendAsync(
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
            _logger.LogError(ex, "Failed to dispatch RoleAssignmentChanged notification for user {UserId} role {Role}", userId, roleName);
        }

        // Trigger sync for Board role changes
        if (string.Equals(roleName, RoleNames.Board, StringComparison.Ordinal))
        {
            await _systemTeamSyncJob.SyncBoardTeamAsync();
        }

        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> EndRoleAsync(
        Guid assignmentId, Guid enderId,
        string? notes,
        CancellationToken ct = default)
    {
        var roleAssignment = await _repository.FindForMutationAsync(assignmentId, ct);

        if (roleAssignment is null)
        {
            return new OnboardingResult(false, "NotFound");
        }

        var now = _clock.GetCurrentInstant();

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

        await _repository.UpdateAsync(roleAssignment, ct);

        await _auditLogService.LogAsync(
            AuditAction.RoleEnded, nameof(User), roleAssignment.UserId,
            $"Role '{roleAssignment.RoleName}' ended",
            enderId);

        _navBadge.Invalidate();
        _claimsInvalidator.Invalidate(roleAssignment.UserId);

        // In-app notification to the user (best-effort)
        try
        {
            await _notificationService.SendAsync(
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
            _logger.LogError(ex, "Failed to dispatch RoleAssignmentChanged notification for user {UserId} role {Role}", roleAssignment.UserId, roleAssignment.RoleName);
        }

        // Trigger sync for Board role changes
        if (string.Equals(roleAssignment.RoleName, RoleNames.Board, StringComparison.Ordinal))
        {
            await _systemTeamSyncJob.SyncBoardTeamAsync();
        }

        return new OnboardingResult(true);
    }

    public Task<bool> IsUserAdminAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _repository.HasActiveRoleAsync(userId, RoleNames.Admin, _clock.GetCurrentInstant(), cancellationToken);

    public Task<bool> IsUserBoardMemberAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _repository.HasActiveRoleAsync(userId, RoleNames.Board, _clock.GetCurrentInstant(), cancellationToken);

    public Task<bool> IsUserTeamsAdminAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _repository.HasActiveRoleAsync(userId, RoleNames.TeamsAdmin, _clock.GetCurrentInstant(), cancellationToken);

    public Task<bool> HasActiveRoleAsync(Guid userId, string roleName, CancellationToken cancellationToken = default) =>
        _repository.HasActiveRoleAsync(userId, roleName, _clock.GetCurrentInstant(), cancellationToken);

    public Task<bool> HasAnyActiveAssignmentAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _repository.HasAnyActiveAssignmentAsync(userId, _clock.GetCurrentInstant(), cancellationToken);

    public Task<IReadOnlyList<Guid>> GetUserIdsWithActiveAssignmentsAsync(CancellationToken cancellationToken = default) =>
        _repository.GetUserIdsWithActiveAssignmentsAsync(_clock.GetCurrentInstant(), cancellationToken);

    public Task<IReadOnlyList<Guid>> GetActiveUserIdsInRoleAsync(
        string roleName, CancellationToken ct = default) =>
        _repository.GetActiveUserIdsInRoleAsync(roleName, _clock.GetCurrentInstant(), ct);

    public Task<IReadOnlyList<(string Role, int Count)>> GetActiveCountsByRoleAsync(
        CancellationToken ct = default) =>
        _repository.GetActiveCountsByRoleAsync(_clock.GetCurrentInstant(), ct);

    public async Task<int> RevokeAllActiveAsync(Guid userId, CancellationToken ct = default)
    {
        var now = _clock.GetCurrentInstant();

        var activeRoles = await _repository.GetActiveForUserForMutationAsync(userId, now, ct);

        if (activeRoles.Count == 0)
            return 0;

        foreach (var role in activeRoles)
        {
            role.ValidTo = now;
        }

        await _repository.UpdateManyAsync(activeRoles, ct);
        _claimsInvalidator.Invalidate(userId);

        return activeRoles.Count;
    }

    public Task<IReadOnlyList<Guid>> GetActiveUserIdsForRoleAsync(
        string roleName, CancellationToken ct = default) =>
        _repository.GetActiveUserIdsForRoleAsync(roleName, _clock.GetCurrentInstant(), ct);

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var assignments = await _repository.GetByUserIdAsync(userId, ct);

        var shaped = assignments.Select(ra => new
        {
            ra.RoleName,
            ValidFrom = ra.ValidFrom.ToInvariantInstantString(),
            ValidTo = ra.ValidTo.ToInvariantInstantString()
        }).ToList();

        return [new UserDataSlice(GdprExportSections.RoleAssignments, shaped)];
    }

    // ==========================================================================
    // Cross-domain nav stitching — populates [Obsolete]-marked nav properties
    // in memory from IUserService calls so controllers/views do not need to
    // change. Design-rules §6b "in-memory join" pattern.
    // ==========================================================================
#pragma warning disable CS0618 // Obsolete cross-domain nav properties populated in-memory

    private async Task StitchCrossDomainNavsAsync(
        IReadOnlyList<RoleAssignment> assignments, CancellationToken ct)
    {
        if (assignments.Count == 0) return;

        var userIds = new HashSet<Guid>();
        foreach (var ra in assignments)
        {
            userIds.Add(ra.UserId);
            userIds.Add(ra.CreatedByUserId);
        }

        var users = await _userService.GetByIdsAsync(userIds, ct);

        foreach (var ra in assignments)
        {
            if (users.TryGetValue(ra.UserId, out var user))
                ra.User = user;

            if (users.TryGetValue(ra.CreatedByUserId, out var creator))
                ra.CreatedByUser = creator;
        }
    }

#pragma warning restore CS0618
}
