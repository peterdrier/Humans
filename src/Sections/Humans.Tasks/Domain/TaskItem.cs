using Humans.Base.Attributes;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.Tasks.Domain;

/// <summary>A piece of work with an end. Named <c>TaskItem</c> because <c>Task</c> is taken by .NET.</summary>
internal sealed class TaskItem
{
    public Guid Id { get; init; }

    /// <summary>A kind some section registers as an owner kind. <c>Team</c> only, for now.</summary>
    public string OwnerKind { get; set; } = string.Empty;

    public Guid OwnerId { get; set; }

    public string Title { get; set; } = string.Empty;

    [MarkdownContent]
    public string? Description { get; set; }

    /// <summary>The Settings event record, by id. Null when the task is not tied to an event.</summary>
    public Guid? EventId { get; set; }

    /// <summary>A fixed start date. At most one of this and <see cref="StartDayOffset"/> is set.</summary>
    public LocalDate? StartDate { get; set; }

    /// <summary>Days from the event's gate opening day (day 0), negative before it. Needs <see cref="EventId"/>.</summary>
    public int? StartDayOffset { get; set; }

    /// <summary>A fixed due date. At most one of this and <see cref="DueDayOffset"/> is set.</summary>
    public LocalDate? DueDate { get; set; }

    /// <summary>Days from the event's gate opening day (day 0), negative before it. Needs <see cref="EventId"/>.</summary>
    public int? DueDayOffset { get; set; }

    public TaskItemStatus Status { get; set; } = TaskItemStatus.Todo;

    /// <summary>What holds the task up. Set while <see cref="Status"/> is Blocked, cleared when it leaves.</summary>
    public string? BlockedNote { get; set; }

    public SlotPriority Priority { get; set; } = SlotPriority.Important;

    /// <summary>How many people it needs. People on it may exceed this.</summary>
    public int SlotCount { get; set; } = 1;

    /// <summary>A claim waits for a manager's answer.</summary>
    public bool NeedsApproval { get; set; }

    /// <summary>Shown beside the claim button. Only kept while <see cref="NeedsApproval"/> is on.</summary>
    public string? ApprovalReason { get; set; }

    public bool IsHidden { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    public ICollection<TaskAssignment> Assignments { get; } = new List<TaskAssignment>();

    /// <summary>Links from this task to the tasks it waits on.</summary>
    public ICollection<TaskDependency> Dependencies { get; } = new List<TaskDependency>();

    /// <summary>Links from other tasks that wait on this one. Only here so deleting a task removes them.</summary>
    public ICollection<TaskDependency> Dependents { get; } = new List<TaskDependency>();
}

internal enum TaskItemStatus
{
    Todo = 0,
    InProgress = 1,
    Blocked = 2,
    Done = 3,
    Cancelled = 4,
}
