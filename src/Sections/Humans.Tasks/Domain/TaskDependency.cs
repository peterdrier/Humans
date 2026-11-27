namespace Humans.Tasks.Domain;

/// <summary>"This task waits on that one." Shown, never enforced.</summary>
internal sealed class TaskDependency
{
    public Guid Id { get; init; }

    /// <summary>The waiting task.</summary>
    public Guid TaskItemId { get; init; }

    public TaskItem TaskItem { get; set; } = null!;

    /// <summary>The task it waits on.</summary>
    public Guid DependsOnTaskItemId { get; init; }

    public TaskItem DependsOn { get; set; } = null!;
}
