using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Auth.Domain;

namespace Humans.Auth.Data;

internal sealed class RoleAssignmentRepository(IDbContextFactory<AuthDbContext> factory) : IRoleAssignmentRepository
{
    public async Task<RoleAssignment?> FindForMutationAsync(Guid assignmentId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.RoleAssignments
            .AsNoTracking()
            .FirstOrDefaultAsync(ra => ra.Id == assignmentId, ct);
    }

    public async Task<RoleAssignment?> GetByIdAsync(Guid assignmentId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.RoleAssignments
            .AsNoTracking()
            .FirstOrDefaultAsync(ra => ra.Id == assignmentId, ct);
    }

    public async Task<IReadOnlyList<RoleAssignment>> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.RoleAssignments
            .AsNoTracking()
            .Where(ra => ra.UserId == userId)
            // arch:db-sort-ok user role history chronology
            .OrderByDescending(ra => ra.ValidFrom)
            .ToListAsync(ct);
    }

    public async Task<(IReadOnlyList<RoleAssignment> Items, int TotalCount)> GetFilteredAsync(
        IReadOnlyList<string>? roleNames,
        bool activeOnly,
        int page,
        int pageSize,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var query = ctx.RoleAssignments.AsNoTracking().AsQueryable();

        if (roleNames is not null)
        {
            query = query.Where(ra => roleNames.Contains(ra.RoleName));
        }

        if (activeOnly)
        {
            query = query.Where(ra => ra.ValidFrom <= now && (ra.ValidTo == null || ra.ValidTo > now));
        }

        var totalCount = await query.CountAsync(ct);

        var items = await query
            // arch:db-sort-ok admin page window over role assignments
            .OrderBy(ra => ra.RoleName)
            .ThenByDescending(ra => ra.ValidFrom) // arch:db-sort-ok admin page window tie-breaker
            .Skip((int)Math.Clamp(((long)page - 1) * pageSize, 0, int.MaxValue))
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task<bool> HasOverlappingAssignmentAsync(
        Guid userId,
        IReadOnlyList<string> roleNames,
        Instant validFrom,
        Instant? validTo,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var query = ctx.RoleAssignments
            .AsNoTracking()
            .Where(ra => ra.UserId == userId && roleNames.Contains(ra.RoleName));

        // [A_start, A_end) overlaps [B_start, B_end) iff A_end > B_start and B_end > A_start; null end is open.
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

    public async Task<bool> HasActiveRoleAsync(
        Guid userId,
        IReadOnlyList<string> roleNames,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.RoleAssignments.AnyAsync(
            ra => ra.UserId == userId &&
                  roleNames.Contains(ra.RoleName) &&
                  ra.ValidFrom <= now &&
                  (ra.ValidTo == null || ra.ValidTo > now),
            ct);
    }

    public async Task<bool> HasAnyActiveAssignmentAsync(
        Guid userId,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.RoleAssignments.AnyAsync(
            ra => ra.UserId == userId &&
                  ra.ValidFrom <= now &&
                  (ra.ValidTo == null || ra.ValidTo > now),
            ct);
    }

    public async Task<IReadOnlyList<Guid>> GetUserIdsWithActiveAssignmentsAsync(
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.RoleAssignments
            .AsNoTracking()
            .Where(ra => ra.ValidFrom <= now && (ra.ValidTo == null || ra.ValidTo > now))
            .Select(ra => ra.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<RoleAssignment>> GetActiveForUserForMutationAsync(
        Guid userId,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.RoleAssignments
            .AsNoTracking()
            .Where(ra => ra.UserId == userId &&
                         ra.ValidFrom <= now &&
                         (ra.ValidTo == null || ra.ValidTo > now))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetActiveUserIdsInRoleAsync(
        IReadOnlyList<string> roleNames,
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.RoleAssignments
            .AsNoTracking()
            .Where(ra =>
                roleNames.Contains(ra.RoleName) &&
                ra.ValidFrom <= now &&
                (ra.ValidTo == null || ra.ValidTo > now))
            .Select(ra => ra.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, int>> GetActiveCountsByRoleAsync(
        Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var rows = await ctx.RoleAssignments
            .AsNoTracking()
            .Where(ra => ra.ValidFrom <= now && (ra.ValidTo == null || ra.ValidTo > now))
            .GroupBy(ra => ra.RoleName)
            .Select(g => new { Role = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Role, r => r.Count, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<RoleAssignment>> GetByRoleNamesForMutationAsync(
        IReadOnlyList<string> roleNames,
        CancellationToken ct = default)
    {
        if (roleNames.Count == 0)
            return [];

        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.RoleAssignments
            .AsNoTracking()
            .Where(ra => roleNames.Contains(ra.RoleName))
            .ToListAsync(ct);
    }

    public async Task AddAsync(RoleAssignment assignment, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.RoleAssignments.Add(assignment);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(RoleAssignment assignment, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        ctx.Attach(assignment);
        ctx.Entry(assignment).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateManyAsync(IReadOnlyList<RoleAssignment> assignments, CancellationToken ct = default)
    {
        if (assignments.Count == 0)
            return;

        await using var ctx = await factory.CreateDbContextAsync(ct);
        foreach (var assignment in assignments)
        {
            ctx.Attach(assignment);
            ctx.Entry(assignment).State = EntityState.Modified;
        }
        await ctx.SaveChangesAsync(ct);
    }

    public async Task ReassignToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        Func<string, string> canonicalName,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var sourceRows = await ctx.RoleAssignments
            .Where(ra => ra.UserId == sourceUserId)
            .ToListAsync(ct);
        var targetRows = await ctx.RoleAssignments
            .Where(ra => ra.UserId == targetUserId)
            .ToListAsync(ct);

        // Same active rule as RoleAssignment.IsActive(updatedAt).
        var targetActiveRoles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tgt in targetRows)
        {
            if (tgt.IsActive(updatedAt))
            {
                targetActiveRoles.Add(canonicalName(tgt.RoleName));
            }
        }

        foreach (var src in sourceRows)
        {
            if (src.IsActive(updatedAt) && targetActiveRoles.Contains(canonicalName(src.RoleName)))
            {
                // Target's active row already covers this role and its lifetime stands.
                ctx.RoleAssignments.Remove(src);
            }
            else
            {
                ctx.Entry(src).Property(nameof(RoleAssignment.UserId)).CurrentValue = targetUserId;
            }
        }

        await ctx.SaveChangesAsync(ct);
    }
}
