using Humans.Tasks.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Humans.Tasks.Data;

internal sealed class TaskRepository(IDbContextFactory<TasksDbContext> factory) : ITaskRepository
{
    public async Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.TaskItems.AsNoTracking()
            .Include(t => t.Assignments)
            .Include(t => t.Dependencies)
            .ToListAsync(ct);
    }

    public async Task AddAsync(TaskItem task, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.TaskItems.Add(task);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(TaskItem task, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Entry(task).State = EntityState.Modified;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid taskId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.TaskItems.Where(t => t.Id == taskId).ExecuteDeleteAsync(ct) > 0;
    }

    public async Task<bool> AddAssignmentAsync(TaskAssignment assignment, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.TaskAssignments.Add(assignment);
        return await TrySaveAsync(db, ct);
    }

    public async Task<bool> ConfirmAssignmentAsync(Guid taskId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.TaskAssignments
            .Where(a => a.TaskItemId == taskId && a.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, TaskAssignmentState.Confirmed), ct) > 0;
    }

    public async Task<bool> RemoveAssignmentAsync(Guid taskId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.TaskAssignments
            .Where(a => a.TaskItemId == taskId && a.UserId == userId)
            .ExecuteDeleteAsync(ct) > 0;
    }

    public async Task<bool> AddDependencyAsync(TaskDependency dependency, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.TaskDependencies.Add(dependency);
        return await TrySaveAsync(db, ct);
    }

    public async Task<bool> RemoveDependencyAsync(Guid taskId, Guid dependsOnTaskId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.TaskDependencies
            .Where(d => d.TaskItemId == taskId && d.DependsOnTaskItemId == dependsOnTaskId)
            .ExecuteDeleteAsync(ct) > 0;
    }

    public async Task RemoveUserAssignmentsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.TaskAssignments.Where(a => a.UserId == userId).ExecuteDeleteAsync(ct);
    }

    public async Task<int> ReassignUserAsync(Guid fromUserId, Guid toUserId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.TaskAssignments.Where(a => a.UserId == fromUserId).ToListAsync(ct);
        if (rows.Count == 0)
            return 0;

        var survivorRows = await db.TaskAssignments
            .Where(a => a.UserId == toUserId)
            .ToDictionaryAsync(a => a.TaskItemId, ct);

        foreach (var row in rows)
        {
            if (survivorRows.TryGetValue(row.TaskItemId, out var kept))
            {
                if (row.State == TaskAssignmentState.Confirmed)
                    kept.State = TaskAssignmentState.Confirmed;
                db.TaskAssignments.Remove(row);
            }
            else
            {
                row.UserId = toUserId;
            }
        }

        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private static async Task<bool> TrySaveAsync(TasksDbContext db, CancellationToken ct)
    {
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
}
