using NodaTime;

namespace Humans.Tasks.Domain;

/// <summary>A person on a task: confirmed, or a request waiting for a manager. Unassigning deletes the row.</summary>
internal sealed class TaskAssignment
{
    public Guid Id { get; init; }

    public Guid TaskItemId { get; init; }

    public TaskItem TaskItem { get; set; } = null!;

    public Guid UserId { get; set; }

    public TaskAssignmentState State { get; set; }

    public Instant CreatedAt { get; init; }
}

internal enum TaskAssignmentState
{
    Requested = 0,
    Confirmed = 1,
}
