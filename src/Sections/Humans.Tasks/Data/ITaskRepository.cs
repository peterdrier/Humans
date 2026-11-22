using Humans.Base.Interfaces.Repositories;
using Humans.Tasks.Domain;

namespace Humans.Tasks.Data;

internal interface ITaskRepository : IRepository
{
    /// <summary>Every task with its assignments and the links to what it waits on, untracked.</summary>
    Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken ct = default);

    Task AddAsync(TaskItem task, CancellationToken ct = default);

    /// <summary>Saves the task's own fields; assignments and links are untouched.</summary>
    Task UpdateAsync(TaskItem task, CancellationToken ct = default);

    /// <summary>Also removes the task's assignments and every link to or from it.</summary>
    Task<bool> DeleteAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>False when the person is already on that task.</summary>
    Task<bool> AddAssignmentAsync(TaskAssignment assignment, CancellationToken ct = default);

    /// <summary>False when the person has no assignment on that task.</summary>
    Task<bool> ConfirmAssignmentAsync(Guid taskId, Guid userId, CancellationToken ct = default);

    Task<bool> RemoveAssignmentAsync(Guid taskId, Guid userId, CancellationToken ct = default);

    /// <summary>False when the link already exists.</summary>
    Task<bool> AddDependencyAsync(TaskDependency dependency, CancellationToken ct = default);

    Task<bool> RemoveDependencyAsync(Guid taskId, Guid dependsOnTaskId, CancellationToken ct = default);

    /// <summary>Removes every assignment and request the person holds.</summary>
    Task RemoveUserAssignmentsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Moves every assignment to the survivor. Where the survivor is already on the task one row is kept, and a
    /// confirmed row wins. Returns how many rows moved or merged.
    /// </summary>
    Task<int> ReassignUserAsync(Guid fromUserId, Guid toUserId, CancellationToken ct = default);
}
