using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="IRoleAssignmentRepository"/>. The
/// only non-test file that writes to <c>DbContext.RoleAssignments</c> after
/// the Auth migration lands. Uses the Scoped + <c>HumansDbContext</c> pattern
/// because Auth writes are rare (admin-driven) and low-traffic (mirrors
/// <see cref="ApplicationRepository"/>).
/// </summary>
public sealed class RoleAssignmentRepository : IRoleAssignmentRepository
{
    private readonly HumansDbContext _dbContext;

    public RoleAssignmentRepository(HumansDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // ==========================================================================
    // Reads
    // ==========================================================================

    public Task<RoleAssignment?> FindForMutationAsync(Guid assignmentId, CancellationToken ct = default) =>
        _dbContext.RoleAssignments.FirstOrDefaultAsync(ra => ra.Id == assignmentId, ct);

    public Task<RoleAssignment?> GetByIdAsync(Guid assignmentId, CancellationToken ct = default) =>
        _dbContext.RoleAssignments
            .AsNoTracking()
            .FirstOrDefaultAsync(ra => ra.Id == assignmentId, ct);

    public async Task<IReadOnlyList<RoleAssignment>> GetByUserIdAsync(Guid userId, CancellationToken ct = default) =>
        await _dbContext.RoleAssignments
            .AsNoTracking()
            .Where(ra => ra.UserId == userId)
            .OrderByDescending(ra => ra.ValidFrom)
            .ToListAsync(ct);

    public async Task<(IReadOnlyList<RoleAssignment> Items, int TotalCount)> GetFilteredAsync(
        string? roleFilter,
        bool activeOnly,
        int page,
        int pageSize,
        Instant now,
        CancellationToken ct = default)
    {
        var query = _dbContext.RoleAssignments.AsNoTracking().AsQueryable();

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

    public async Task<bool> HasOverlappingAssignmentAsync(
        Guid userId,
        string roleName,
        Instant validFrom,
        Instant? validTo,
        CancellationToken ct = default)
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

        return await query.AnyAsync(ct);
    }

    public Task<bool> HasActiveRoleAsync(
        Guid userId,
        string roleName,
        Instant now,
        CancellationToken ct = default) =>
        _dbContext.RoleAssignments.AnyAsync(
            ra => ra.UserId == userId &&
                  ra.RoleName == roleName &&
                  ra.ValidFrom <= now &&
                  (ra.ValidTo == null || ra.ValidTo > now),
            ct);

    public Task<bool> HasAnyActiveAssignmentAsync(
        Guid userId,
        Instant now,
        CancellationToken ct = default) =>
        _dbContext.RoleAssignments.AnyAsync(
            ra => ra.UserId == userId &&
                  ra.ValidFrom <= now &&
                  (ra.ValidTo == null || ra.ValidTo > now),
            ct);

    public async Task<IReadOnlyList<Guid>> GetUserIdsWithActiveAssignmentsAsync(
        Instant now,
        CancellationToken ct = default) =>
        await _dbContext.RoleAssignments
            .AsNoTracking()
            .Where(ra => ra.ValidFrom <= now && (ra.ValidTo == null || ra.ValidTo > now))
            .Select(ra => ra.UserId)
            .Distinct()
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> GetActiveUserIdsForRoleAsync(
        string roleName,
        Instant now,
        CancellationToken ct = default) =>
        await _dbContext.RoleAssignments
            .AsNoTracking()
            .Where(ra => ra.RoleName == roleName &&
                         ra.ValidFrom <= now &&
                         (ra.ValidTo == null || ra.ValidTo > now))
            .Select(ra => ra.UserId)
            .Distinct()
            .ToListAsync(ct);

    public async Task<IReadOnlyList<RoleAssignment>> GetActiveForUserForMutationAsync(
        Guid userId,
        Instant now,
        CancellationToken ct = default) =>
        await _dbContext.RoleAssignments
            .Where(ra => ra.UserId == userId &&
                         ra.ValidFrom <= now &&
                         (ra.ValidTo == null || ra.ValidTo > now))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> GetActiveUserIdsInRoleAsync(
        string roleName,
        Instant now,
        CancellationToken ct = default) =>
        await _dbContext.RoleAssignments
            .AsNoTracking()
            .Where(ra =>
                ra.RoleName == roleName &&
                ra.ValidFrom <= now &&
                (ra.ValidTo == null || ra.ValidTo > now))
            .Select(ra => ra.UserId)
            .Distinct()
            .ToListAsync(ct);

    // ==========================================================================
    // Writes
    // ==========================================================================

    public async Task AddAsync(RoleAssignment assignment, CancellationToken ct = default)
    {
        _dbContext.RoleAssignments.Add(assignment);
        await _dbContext.SaveChangesAsync(ct);
    }

    public Task SaveTrackedAsync(CancellationToken ct = default) =>
        _dbContext.SaveChangesAsync(ct);
}
