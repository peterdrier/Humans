using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Tasks.Domain;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.Tasks.Services;

/// <summary>
/// Tasks' whole surface, inside the section. Auth-free: controllers ask <c>IPermissionService</c> (through
/// <see cref="Authorization.TasksAccess"/>) before calling a write, and filter reads with it.
/// </summary>
internal interface ITaskService : IApplicationService
{
    /// <summary>Every task, ordered by title. Callers filter what the viewer may see.</summary>
    Task<IReadOnlyList<TaskInfo>> GetTasksAsync(CancellationToken ct = default);

    /// <summary>The default list: the active event's tasks plus tasks with no event, without done and cancelled ones.</summary>
    Task<IReadOnlyList<TaskInfo>> GetListedTasksAsync(CancellationToken ct = default);

    Task<TaskInfo?> GetTaskAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>One row per team with listed tasks, and every listed blocked task. For the read-only admin page.</summary>
    Task<TasksAdminSummary> GetAdminSummaryAsync(CancellationToken ct = default);

    /// <summary>A new task starts as to do. Needs <c>Tasks.Manage</c> on <see cref="TaskInput.TeamId"/>.</summary>
    Task<TaskResult> CreateAsync(TaskInput input, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Needs <c>Tasks.Manage</c> on the task's team, and on <see cref="TaskInput.TeamId"/> when that moves it.</summary>
    Task<TaskResult> UpdateAsync(Guid taskId, TaskInput input, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Blocked needs a note, and leaving Blocked clears it. Needs <c>Tasks.Manage</c>, or a confirmed place on the task
    /// for any status but cancelled.
    /// </summary>
    Task<TaskResult> SetStatusAsync(Guid taskId, TaskItemStatus status, string? blockedNote, Guid actorUserId, CancellationToken ct = default);

    Task<TaskResult> DeleteAsync(Guid taskId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Confirms the person, answering their request if they made one. May exceed the slots.</summary>
    Task<TaskResult> AssignAsync(Guid taskId, Guid userId, Guid actorUserId, CancellationToken ct = default);

    Task<TaskResult> UnassignAsync(Guid taskId, Guid userId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>"<paramref name="taskId"/> waits on <paramref name="dependsOnTaskId"/>". Needs <c>Tasks.Manage</c> on the waiting task's team.</summary>
    Task<TaskResult> AddDependencyAsync(Guid taskId, Guid dependsOnTaskId, Guid actorUserId, CancellationToken ct = default);

    Task<TaskResult> RemoveDependencyAsync(Guid taskId, Guid dependsOnTaskId, Guid actorUserId, CancellationToken ct = default);
}

/// <summary>What a task form sends.</summary>
/// <param name="TeamId">An active, non-system team.</param>
/// <param name="Title">Required, at most 200 characters.</param>
/// <param name="Description">Markdown.</param>
/// <param name="EventId">The Settings event record, or null for none.</param>
/// <param name="Start">Null for none.</param>
/// <param name="Due">Null for none.</param>
/// <param name="Priority">How much the task matters.</param>
/// <param name="SlotCount">How many people it needs, at least 1.</param>
/// <param name="NeedsApproval">A claim waits for a manager's answer.</param>
/// <param name="ApprovalReason">Dropped when <paramref name="NeedsApproval"/> is off.</param>
/// <param name="IsHidden">Shown only to the team's managers, people on it, and those who see all.</param>
internal sealed record TaskInput(
    Guid TeamId,
    string Title,
    string? Description,
    Guid? EventId,
    TaskDateInput? Start,
    TaskDateInput? Due,
    SlotPriority Priority,
    int SlotCount,
    bool NeedsApproval,
    string? ApprovalReason,
    bool IsHidden);

/// <summary>
/// A date as the form picks it: always a calendar date. Tasks works out the day number for a relative one. On an
/// edit, a relative date equal to what the task already shows keeps its stored day number, so changing the event alone
/// keeps the day numbers.
/// </summary>
/// <param name="IsRelative">Stored as days from the event's gate opening day instead of as the date.</param>
/// <param name="Date">The calendar date picked.</param>
internal sealed record TaskDateInput(bool IsRelative, LocalDate Date);

/// <summary>A stored date: fixed, or days from the event's gate opening day.</summary>
/// <param name="Fixed">Set for a fixed date.</param>
/// <param name="DayOffset">Set for a relative one: day 0 is gate opening, before it is negative.</param>
/// <param name="Resolved">The calendar date shown. Null for a relative date whose event is unknown.</param>
internal sealed record TaskDateInfo(LocalDate? Fixed, int? DayOffset, LocalDate? Resolved)
{
    public bool IsRelative => DayOffset is not null;
}

/// <summary>A person on a task.</summary>
/// <param name="UserId">The person.</param>
/// <param name="IsConfirmed">False for a request waiting for a manager.</param>
/// <param name="CreatedAt">When they were put or asked to be on it.</param>
internal sealed record TaskAssignmentInfo(Guid UserId, bool IsConfirmed, Instant CreatedAt);

/// <summary>A task this one waits on.</summary>
/// <param name="Id">The other task.</param>
/// <param name="Title">Its title. Show "a hidden task" when the viewer cannot see it.</param>
/// <param name="Status">Its status.</param>
internal sealed record TaskLinkInfo(Guid Id, string Title, TaskItemStatus Status);

/// <summary>One task as the pages show it.</summary>
/// <param name="Id">The task.</param>
/// <param name="Owner">The team, as owner kind plus id.</param>
/// <param name="TeamName">Empty when the team is gone.</param>
/// <param name="TeamSlug">Empty when the team is gone.</param>
/// <param name="TeamIsHidden">Every task of a hidden team counts as hidden.</param>
/// <param name="Title">Task title.</param>
/// <param name="Description">Markdown.</param>
/// <param name="EventId">Null when not tied to an event.</param>
/// <param name="EventName">Null when not tied to an event.</param>
/// <param name="Start">Null for none.</param>
/// <param name="Due">Null for none.</param>
/// <param name="Status">To do, in progress, blocked, done or cancelled.</param>
/// <param name="BlockedNote">What holds it up. Only while blocked.</param>
/// <param name="Priority">How much the task matters.</param>
/// <param name="SlotCount">How many people it needs.</param>
/// <param name="NeedsApproval">A claim waits for a manager's answer.</param>
/// <param name="ApprovalReason">Shown beside the claim button.</param>
/// <param name="IsHidden">The task's own switch. See <see cref="TaskInfo.IsEffectivelyHidden"/>.</param>
/// <param name="Assignments">Confirmed people and requests, oldest first.</param>
/// <param name="WaitingOn">The tasks it depends on, whatever their state.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="UpdatedAt">When it last changed.</param>
internal sealed record TaskInfo(
    Guid Id,
    Owner Owner,
    string TeamName,
    string TeamSlug,
    bool TeamIsHidden,
    string Title,
    string? Description,
    Guid? EventId,
    string? EventName,
    TaskDateInfo? Start,
    TaskDateInfo? Due,
    TaskItemStatus Status,
    string? BlockedNote,
    SlotPriority Priority,
    int SlotCount,
    bool NeedsApproval,
    string? ApprovalReason,
    bool IsHidden,
    IReadOnlyList<TaskAssignmentInfo> Assignments,
    IReadOnlyList<TaskLinkInfo> WaitingOn,
    Instant CreatedAt,
    Instant UpdatedAt)
{
    public bool IsEffectivelyHidden => IsHidden || TeamIsHidden;

    public int ConfirmedCount => Assignments.Count(a => a.IsConfirmed);

    /// <summary>Slots minus confirmed people, never below zero. Requests take no slot.</summary>
    public int OpenSlots => Math.Max(0, SlotCount - ConfirmedCount);

    /// <summary>Something it waits on is not finished. A cancelled task holds nothing up.</summary>
    public bool IsWaiting => WaitingOn.Any(l => l.Status is not (TaskItemStatus.Done or TaskItemStatus.Cancelled));

    public bool IsFinished => Status is TaskItemStatus.Done or TaskItemStatus.Cancelled;
}

/// <summary>What a write answers.</summary>
/// <param name="Task">The task after the write. Null after a delete and on a refusal.</param>
/// <param name="Refusal">Why not, in the current UI culture. Null on success.</param>
/// <param name="Removed">What a delete removed.</param>
internal sealed record TaskResult(TaskInfo? Task = null, string? Refusal = null, TaskInfo? Removed = null)
{
    public bool Succeeded => Refusal is null;
}

/// <summary>Counts over the listed tasks of one team.</summary>
/// <param name="TeamId">The team.</param>
/// <param name="TeamName">Team name.</param>
/// <param name="TeamSlug">Team slug.</param>
/// <param name="Tasks">Listed tasks on the team.</param>
/// <param name="OpenSlots">Slots minus confirmed people, summed, never below zero per task.</param>
/// <param name="BlockedTasks">Listed tasks that are blocked.</param>
internal sealed record TeamTaskCounts(Guid TeamId, string TeamName, string TeamSlug, int Tasks, int OpenSlots, int BlockedTasks);

/// <summary>The read-only admin page.</summary>
/// <param name="Teams">One row per team that has listed tasks.</param>
/// <param name="BlockedTasks">Every listed blocked task, with its note.</param>
internal sealed record TasksAdminSummary(IReadOnlyList<TeamTaskCounts> Teams, IReadOnlyList<TaskInfo> BlockedTasks);
