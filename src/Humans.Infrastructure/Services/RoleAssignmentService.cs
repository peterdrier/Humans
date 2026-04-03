using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Role assignment validation/query service.
/// </summary>
public class RoleAssignmentService : IRoleAssignmentService
{
    private readonly HumansDbContext _dbContext;
    private readonly IAuditLogService _auditLogService;
    private readonly ISystemTeamSync _systemTeamSyncJob;
    private readonly IClock _clock;
    private readonly IMemoryCache _cache;
    private readonly ILogger<RoleAssignmentService> _logger;

    public RoleAssignmentService(
        HumansDbContext dbContext,
        IAuditLogService auditLogService,
        ISystemTeamSync systemTeamSyncJob,
        IClock clock,
        IMemoryCache cache,
        ILogger<RoleAssignmentService> logger)
    {
        _dbContext = dbContext;
        _auditLogService = auditLogService;
        _systemTeamSyncJob = systemTeamSyncJob;
        _clock = clock;
        _cache = cache;
        _logger = logger;
    }

    public async Task<bool> HasOverlappingAssignmentAsync(
        Guid userId,
        string roleName,
        Instant validFrom,
        Instant? validTo = null,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.RoleAssignments
            .AsNoTracking()
            .Where(ra => ra.UserId == userId && ra.RoleName == roleName);

        // Overlap predicate:
        // [A_start, A_end) overlaps [B_start, B_end) iff
        // A_end > B_start AND B_end > A_start.
        // Null end means open-ended.
        if (validTo.HasValue)
        {
            query = query.Where(ra =>
                (ra.ValidTo == null || ra.ValidTo > validFrom) &&
                validTo.Value > ra.ValidFrom);
        }
        else
        {
            query = query.Where(ra => ra.ValidTo == null || ra.ValidTo > validFrom);
        }

        return await query.AnyAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<RoleAssignment> Items, int TotalCount)> GetFilteredAsync(
        string? roleFilter, bool activeOnly, int page, int pageSize, Instant now,
        CancellationToken ct = default)
    {
        var query = _dbContext.RoleAssignments
            .AsNoTracking()
            .Include(ra => ra.User)
            .Include(ra => ra.CreatedByUser)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(roleFilter))
        {
            query = query.Where(ra => ra.RoleName == roleFilter);
        }

        if (activeOnly)
        {
            query = query.Where(ra => ra.ValidFrom <= now && (ra.ValidTo == null || ra.ValidTo > now));
        }

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderBy(ra => ra.RoleName)
            .ThenByDescending(ra => ra.ValidFrom)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task<RoleAssignment?> GetByIdAsync(Guid assignmentId, CancellationToken ct = default)
    {
        return await _dbContext.RoleAssignments
            .Include(ra => ra.User)
            .FirstOrDefaultAsync(ra => ra.Id == assignmentId, ct);
    }

    public async Task<IReadOnlyList<RoleAssignment>> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        return await _dbContext.RoleAssignments
            .AsNoTracking()
            .Include(ra => ra.CreatedByUser)
            .Where(ra => ra.UserId == userId)
            .OrderByDescending(ra => ra.ValidFrom)
            .ToListAsync(ct);
    }

    public async Task<OnboardingResult> AssignRoleAsync(
        Guid userId, string roleName, Guid assignerId,
        string? notes, CancellationToken ct = default)
    {
        var now = _clock.GetCurrentInstant();

        var hasOverlap = await HasOverlappingAssignmentAsync(userId, roleName, now, cancellationToken: ct);
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

        _dbContext.RoleAssignments.Add(roleAssignment);

        var user = await _dbContext.Users.FindAsync([userId], ct);

        await _auditLogService.LogAsync(
            AuditAction.RoleAssigned, nameof(User), userId,
            $"Role '{roleName}' assigned",
            assignerId);

        await _dbContext.SaveChangesAsync(ct);
        _cache.InvalidateNavBadgeCounts();
        _cache.InvalidateRoleAssignmentClaims(userId);

        _logger.LogInformation("Admin {AdminId} assigned role {Role} to user {UserId}",
            assignerId, roleName, userId);

        // Trigger sync for Board role changes
        if (string.Equals(roleName, RoleNames.Board, StringComparison.Ordinal))
        {
            await _systemTeamSyncJob.SyncBoardTeamAsync();
        }

        return new OnboardingResult(true);
    }

    public async Task<OnboardingResult> EndRoleAsync(
        Guid assignmentId, Guid enderId,
        string? notes, CancellationToken ct = default)
    {
        var roleAssignment = await _dbContext.RoleAssignments
            .Include(ra => ra.User)
            .FirstOrDefaultAsync(ra => ra.Id == assignmentId, ct);

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

        await _auditLogService.LogAsync(
            AuditAction.RoleEnded, nameof(User), roleAssignment.UserId,
            $"Role '{roleAssignment.RoleName}' ended",
            enderId);

        await _dbContext.SaveChangesAsync(ct);
        _cache.InvalidateNavBadgeCounts();
        _cache.InvalidateRoleAssignmentClaims(roleAssignment.UserId);

        _logger.LogInformation("Admin {AdminId} ended role {Role} for user {UserId}",
            enderId, roleAssignment.RoleName, roleAssignment.UserId);

        // Trigger sync for Board role changes
        if (string.Equals(roleAssignment.RoleName, RoleNames.Board, StringComparison.Ordinal))
        {
            await _systemTeamSyncJob.SyncBoardTeamAsync();
        }

        return new OnboardingResult(true);
    }

    public async Task<bool> IsUserAdminAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        return await _dbContext.RoleAssignments
            .AnyAsync(ra =>
                ra.UserId == userId &&
                ra.RoleName == RoleNames.Admin &&
                ra.ValidFrom <= now &&
                (ra.ValidTo == null || ra.ValidTo > now),
                cancellationToken);
    }

    public async Task<bool> IsUserBoardMemberAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        return await _dbContext.RoleAssignments
            .AnyAsync(ra =>
                ra.UserId == userId &&
                ra.RoleName == RoleNames.Board &&
                ra.ValidFrom <= now &&
                (ra.ValidTo == null || ra.ValidTo > now),
                cancellationToken);
    }

    public async Task<bool> IsUserTeamsAdminAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        return await _dbContext.RoleAssignments
            .AnyAsync(ra =>
                ra.UserId == userId &&
                ra.RoleName == RoleNames.TeamsAdmin &&
                ra.ValidFrom <= now &&
                (ra.ValidTo == null || ra.ValidTo > now),
                cancellationToken);
    }
}
