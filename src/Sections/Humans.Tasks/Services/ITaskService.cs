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
    /// <summary>Every task. Callers filter what the viewer may see, and sort.</summary>
    Task<IReadOnlyList<TaskInfo>> GetTasksAsync(CancellationToken ct = default);

    /// <summary>
    /// Of <paramref name="all"/> (from <see cref="GetTasksAsync"/>, so a page reads the tasks once), the ones a list
    /// shows; a null scope is the default <see cref="TaskListScope"/>. Callers filter what the viewer may see, and sort.
    /// </summary>
    IReadOnlyList<TaskInfo> Listed(IEnumerable<TaskInfo> all, TaskListScope? scope = null);

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

    /// <summary>Confirms a known person not yet on the task. May exceed the slots.</summary>
    Task<TaskResult> AssignAsync(Guid taskId, Guid userId, Guid actorUserId, CancellationToken ct = default);

    Task<TaskResult> UnassignAsync(Guid taskId, Guid userId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// The actor takes a free slot. Confirms at once, or makes a request (no slot taken) when the task needs approval.
    /// Refused on a full, done or cancelled task and when the actor is already on it. Needs only a signed-in human who
    /// can see the task. Never adds team membership.
    /// </summary>
    Task<TaskResult> ClaimAsync(Guid taskId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>The actor gives back their own confirmed place. Needs only that place.</summary>
    Task<TaskResult> ReleaseAsync(Guid taskId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>The actor takes back their own request. Needs only that request.</summary>
    Task<TaskResult> WithdrawRequestAsync(Guid taskId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Confirms <paramref name="userId"/>'s request, overfilling a task that has since filled up, and emails them.
    /// Needs <c>Tasks.Manage</c> on the task's team.
    /// </summary>
    Task<TaskResult> ApproveRequestAsync(Guid taskId, Guid userId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Removes <paramref name="userId"/>'s request and emails them; they may ask again. The optional reason (200
    /// characters at most) goes only to the email and the audit entry. Needs <c>Tasks.Manage</c> on the task's team.
    /// </summary>
    Task<TaskResult> DeclineRequestAsync(Guid taskId, Guid userId, string? reason, Guid actorUserId, CancellationToken ct = default);

    /// <summary>"<paramref name="taskId"/> waits on <paramref name="dependsOnTaskId"/>". Needs <c>Tasks.Manage</c> on the waiting task's team.</summary>
    Task<TaskResult> AddDependencyAsync(Guid taskId, Guid dependsOnTaskId, Guid actorUserId, CancellationToken ct = default);

    Task<TaskResult> RemoveDependencyAsync(Guid taskId, Guid dependsOnTaskId, Guid actorUserId, CancellationToken ct = default);
}

/// <summary>Which tasks a list shows. The default is the active event's tasks plus tasks with no event, without done and cancelled ones.</summary>
/// <param name="AnyEvent">Every event's tasks and those with none.</param>
/// <param name="EventId">Only this event's tasks. Ignored with <paramref name="AnyEvent"/>.</param>
/// <param name="IncludeFinished">Keep done and cancelled tasks.</param>
internal sealed record TaskListScope(bool AnyEvent = false, Guid? EventId = null, bool IncludeFinished = false);

/// <summary>
/// What a task form sends. The team is active and not a system team; the title is required, at most 200 characters;
/// the description is Markdown; a null event or date means none; the approval reason is dropped when needs-approval
/// is off.
/// </summary>
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
/// A date as the form picks it: always a calendar date, stored as days from the event's gate opening day when
/// relative. On an edit, a relative date equal to what the task already shows keeps its stored day number, so changing
/// the event alone keeps the day numbers.
/// </summary>
internal sealed record TaskDateInput(bool IsRelative, LocalDate Date);

/// <summary>A stored date: fixed, or days from the event's gate opening day.</summary>
/// <param name="Fixed">Set for a fixed date.</param>
/// <param name="DayOffset">Set for a relative one: day 0 is gate opening, before it is negative.</param>
/// <param name="Resolved">The calendar date shown. Null for a relative date whose event is unknown.</param>
internal sealed record TaskDateInfo(LocalDate? Fixed, int? DayOffset, LocalDate? Resolved)
{
    public bool IsRelative => DayOffset is not null;
}

/// <summary>A person on a task: confirmed, or a request waiting for a manager.</summary>
internal sealed record TaskAssignmentInfo(Guid UserId, bool IsConfirmed, Instant CreatedAt);

/// <summary>A task this one waits on. Show "a hidden task" in place of the title when the viewer cannot see it.</summary>
internal sealed record TaskLinkInfo(Guid Id, string Title, TaskItemStatus Status);

/// <summary>
/// One task as the pages show it. The owner is the team; team name and slug are empty when the team is gone. The
/// description is Markdown; the blocked note is set only while blocked. Assignments are confirmed people and requests,
/// oldest first; waiting-on lists what it depends on, whatever its state.
/// </summary>
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
    IReadOnlyList<TaskLinkInfo> WaitingOn)
{
    public bool IsEffectivelyHidden => IsHidden || TeamIsHidden;

    public int ConfirmedCount => Assignments.Count(a => a.IsConfirmed);

    /// <summary>Slots minus confirmed people, never below zero. Requests take no slot.</summary>
    public int OpenSlots => Math.Max(0, SlotCount - ConfirmedCount);

    /// <summary>Requests waiting for a manager's answer, oldest first, on a finished task too: it still owes an answer.</summary>
    public IEnumerable<TaskAssignmentInfo> WaitingRequests => Assignments.Where(a => !a.IsConfirmed).OrderBy(a => a.CreatedAt);

    /// <summary>An unfinished task with something it waits on not finished. A cancelled task holds nothing up.</summary>
    public bool IsWaiting => !IsFinished && WaitingOn.Any(l => l.Status is not (TaskItemStatus.Done or TaskItemStatus.Cancelled));

    public bool IsFinished => Status is TaskItemStatus.Done or TaskItemStatus.Cancelled;

    /// <summary>No start and no due date, as shown: a relative date with no event to resolve against counts as none.</summary>
    public bool IsUndated => Start?.Resolved is null && Due?.Resolved is null;
}

/// <summary>What a write answers.</summary>
/// <param name="Task">The task after the write. Null after a delete and on a refusal.</param>
/// <param name="Refusal">Why not, in the current UI culture. Null on success.</param>
/// <param name="Removed">What a delete removed.</param>
internal sealed record TaskResult(TaskInfo? Task = null, string? Refusal = null, TaskInfo? Removed = null)
{
    public bool Succeeded => Refusal is null;
}

/// <summary>Counts over the listed tasks of one team. Open slots are summed, never below zero per task.</summary>
internal sealed record TeamTaskCounts(string TeamName, string TeamSlug, int Tasks, int OpenSlots, int BlockedTasks);

/// <summary>The read-only admin page.</summary>
/// <param name="Teams">One row per team that has listed tasks.</param>
/// <param name="BlockedTasks">Every listed blocked task, with its note.</param>
internal sealed record TasksAdminSummary(IReadOnlyList<TeamTaskCounts> Teams, IReadOnlyList<TaskInfo> BlockedTasks);
