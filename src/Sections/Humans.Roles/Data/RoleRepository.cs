using Humans.Roles.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Humans.Roles.Data;

internal sealed class RoleRepository(IDbContextFactory<RolesDbContext> factory) : IRoleRepository
{
    public async Task<IReadOnlyList<Role>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Roles.AsNoTracking().Include(r => r.Assignments).ToListAsync(ct);
    }

    public async Task AddAsync(Role role, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Role role, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Entry(role).State = EntityState.Modified;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid roleId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Roles.Where(r => r.Id == roleId).ExecuteDeleteAsync(ct) > 0;
    }

    public async Task<bool> AddAssignmentAsync(RoleAssignment assignment, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.RoleAssignments.Add(assignment);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return false;
        }
    }

    public async Task<bool> RemoveAssignmentAsync(Guid roleId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.RoleAssignments
            .Where(a => a.RoleId == roleId && a.UserId == userId)
            .ExecuteDeleteAsync(ct) > 0;
    }

    public async Task<IReadOnlyList<Guid>> RemoveAssignmentsAsync(
        Guid userId, IReadOnlyCollection<Guid>? roleIds, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.RoleAssignments
            .Where(a => a.UserId == userId && (roleIds == null || roleIds.Contains(a.RoleId)))
            .ToListAsync(ct);
        db.RoleAssignments.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Select(a => a.RoleId).ToList();
    }

    public async Task<int> ReassignUserAsync(Guid fromUserId, Guid toUserId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.RoleAssignments.Where(a => a.UserId == fromUserId).ToListAsync(ct);
        if (rows.Count == 0)
            return 0;

        var survivorRoleIds = await db.RoleAssignments
            .Where(a => a.UserId == toUserId)
            .Select(a => a.RoleId)
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            if (survivorRoleIds.Contains(row.RoleId))
                db.RoleAssignments.Remove(row);
            else
                row.UserId = toUserId;
        }

        await db.SaveChangesAsync(ct);
        return rows.Count;
    }
}
