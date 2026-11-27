using Humans.Tasks.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Humans.Tasks.Data;

internal sealed class TaskRepository(IDbContextFactory<TasksDbContext> factory, ILogger<TaskRepository> logger) : ITaskRepository
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

    public async Task AddManyAsync(
        IReadOnlyList<TaskItem> tasks, IReadOnlyList<TaskDependency> dependencies, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.TaskItems.AddRange(tasks);
        db.TaskDependencies.AddRange(dependencies);
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
        return await TrySaveAsync(db, "person", assignment.TaskItemId, assignment.UserId, ct);
    }

    public async Task<bool> RemoveAssignmentAsync(Guid taskId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.TaskAssignments
            .Where(a => a.TaskItemId == taskId && a.UserId == userId)
            .ExecuteDeleteAsync(ct) > 0;
    }

    public async Task<bool> ConfirmRequestAsync(Guid taskId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var request = await RequestAsync(db, taskId, userId, ct);
        if (request is null)
            return false;

        request.State = TaskAssignmentState.Confirmed;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveRequestAsync(Guid taskId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var request = await RequestAsync(db, taskId, userId, ct);
        if (request is null)
            return false;

        db.TaskAssignments.Remove(request);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> AddDependencyAsync(TaskDependency dependency, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.TaskDependencies.Add(dependency);
        return await TrySaveAsync(db, "dependency on task", dependency.TaskItemId, dependency.DependsOnTaskItemId, ct);
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

    private static Task<TaskAssignment?> RequestAsync(TasksDbContext db, Guid taskId, Guid userId, CancellationToken ct) =>
        db.TaskAssignments.FirstOrDefaultAsync(
            a => a.TaskItemId == taskId && a.UserId == userId && a.State == TaskAssignmentState.Requested, ct);

    /// <summary>False, logged, when the row already exists.</summary>
    private async Task<bool> TrySaveAsync(TasksDbContext db, string what, Guid taskId, Guid otherId, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            logger.LogWarning("Task {TaskId} already has {What} {OtherId}; the add was refused", taskId, what, otherId);
            return false;
        }
    }
}
