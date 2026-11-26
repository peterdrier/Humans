using Humans.Audit.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Extensions;
using Humans.Gdpr.Contracts;
using Humans.Settings.Contracts;
using Humans.Tasks.Contracts;
using Humans.Tasks.Data;
using Humans.Tasks.Domain;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Tasks.Services;

/// <summary>
/// Task rules. Every write is audited here. Holds no permission checks: controllers ask first, through
/// <see cref="Authorization.TasksAccess"/>. Team names come from <see cref="ITeamService"/> and event names and gate
/// opening dates from <see cref="ISettingsService"/>.
/// </summary>
internal sealed partial class TaskService(
    ITaskRepository repository,
    ITeamService teams,
    ISettingsService settings,
    IUserService users,
    IAuditLogService audit,
    IClock clock,
    IStringLocalizer<TasksResource> localizer,
    ILogger<TaskService> logger) : ITaskService, ITaskWrite, IUserDataContributor, IUserMerge
{
    internal const string ExportSection = "Tasks";
    internal const string AuditSubjectType = "TaskItem";

    internal const int TitleMaxLength = 200;
    internal const int DescriptionMaxLength = 4000;
    internal const int NoteMaxLength = 500;
    internal const int ReasonMaxLength = 200;

    // Static: the erasure-coverage architecture test reads it from an uninitialized instance.
    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal) { [ExportSection] = null };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public async Task<IReadOnlyList<TaskInfo>> GetTasksAsync(CancellationToken ct = default)
    {
        var snapshot = await LoadAsync(ct);
        return snapshot.Tasks.Values.Select(t => ToInfo(t, snapshot)).ToList();
    }

    public async Task<IReadOnlyList<TaskInfo>> GetListedTasksAsync(TaskListScope? scope = null, CancellationToken ct = default)
    {
        scope ??= new TaskListScope();
        var activeEventId = settings.GetActiveEventSettings()?.Id;
        return (await GetTasksAsync(ct))
            .Where(t => (scope.IncludeFinished || !t.IsFinished)
                && (scope.AnyEvent
                    || (scope.EventId is { } eventId ? t.EventId == eventId : t.EventId is null || t.EventId == activeEventId)))
            .ToList();
    }

    public async Task<TaskInfo?> GetTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        var snapshot = await LoadAsync(ct);
        return snapshot.Tasks.TryGetValue(taskId, out var task) ? ToInfo(task, snapshot) : null;
    }

    public async Task<TasksAdminSummary> GetAdminSummaryAsync(CancellationToken ct = default)
    {
        var listed = await GetListedTasksAsync(ct: ct);
        var rows = listed
            .GroupBy(t => t.Owner)
            .Select(g => new TeamTaskCounts(
                g.Key.Id,
                g.First().TeamName,
                g.First().TeamSlug,
                g.Count(),
                g.Sum(t => t.OpenSlots),
                g.Count(t => t.Status == TaskItemStatus.Blocked)))
            .ToList();
        var blocked = listed.Where(t => t.Status == TaskItemStatus.Blocked).ToList();
        return new TasksAdminSummary(rows, blocked);
    }

    public async Task<TaskResult> CreateAsync(TaskInput input, Guid actorUserId, CancellationToken ct = default)
    {
        var snapshot = await LoadAsync(ct);
        if (await TeamRefusalAsync(input.TeamId, ct) is { } teamRefusal)
            return Refuse(teamRefusal, actorUserId);
        if (InputRefusal(input) is { } refusal)
            return Refuse(refusal, actorUserId);
        if (EventRefusal(input.EventId, currentEventId: null, snapshot) is { } eventRefusal)
            return Refuse(eventRefusal, actorUserId);

        var newEvent = EventOf(input.EventId, snapshot);
        var (start, startRefusal) = ResolveDate(input.Start, currentOffset: null, oldEvent: null, newEvent);
        var (due, dueRefusal) = ResolveDate(input.Due, currentOffset: null, oldEvent: null, newEvent);
        var dateRefusal = startRefusal ?? dueRefusal ?? OrderRefusal(start, due, newEvent);
        if (dateRefusal is not null)
            return Refuse(dateRefusal, actorUserId);

        var now = clock.GetCurrentInstant();
        var task = new TaskItem
        {
            Id = Guid.NewGuid(),
            OwnerKind = TeamOwner.Kind,
            CreatedAt = now,
        };
        Apply(task, input, start, due, now);
        await repository.AddAsync(task, ct);
        snapshot.Tasks[task.Id] = task;

        await audit.LogAsync(TasksAuditActions.TaskCreated, AuditSubjectType, task.Id,
            $"Task '{task.Title}' created on {TeamName(task, snapshot)}",
            actorUserId, targetId: task.OwnerId, targetType: task.OwnerKind);

        return new(ToInfo(task, snapshot));
    }

    public async Task<TaskResult> UpdateAsync(Guid taskId, TaskInput input, Guid actorUserId, CancellationToken ct = default)
    {
        var snapshot = await LoadAsync(ct);
        if (!snapshot.Tasks.TryGetValue(taskId, out var task))
            return Refuse("Tasks_Refusal_NoTask", actorUserId, taskId);
        if (input.TeamId != task.OwnerId && await TeamRefusalAsync(input.TeamId, ct) is { } teamRefusal)
            return Refuse(teamRefusal, actorUserId, taskId);
        if (InputRefusal(input) is { } refusal)
            return Refuse(refusal, actorUserId, taskId);
        if (EventRefusal(input.EventId, task.EventId, snapshot) is { } eventRefusal)
            return Refuse(eventRefusal, actorUserId, taskId);

        var oldEvent = EventOf(task.EventId, snapshot);
        var newEvent = EventOf(input.EventId, snapshot);
        var (start, startRefusal) = ResolveDate(input.Start, task.StartDayOffset, oldEvent, newEvent);
        var (due, dueRefusal) = ResolveDate(input.Due, task.DueDayOffset, oldEvent, newEvent);
        var dateRefusal = startRefusal ?? dueRefusal ?? OrderRefusal(start, due, newEvent);
        if (dateRefusal is not null)
            return Refuse(dateRefusal, actorUserId, taskId);

        var changes = Changes(task, input, start, due);
        var fromTeam = TeamName(task, snapshot);
        Apply(task, input, start, due, clock.GetCurrentInstant());
        await repository.UpdateAsync(task, ct);

        var moved = changes.Contains("team", StringComparer.Ordinal)
            ? $"; moved from {fromTeam} to {TeamName(task, snapshot)}"
            : string.Empty;
        await audit.LogAsync(TasksAuditActions.TaskUpdated, AuditSubjectType, task.Id,
            $"Task '{task.Title}' on {TeamName(task, snapshot)} updated ({(changes.Count == 0 ? "nothing changed" : string.Join(", ", changes))}){moved}",
            actorUserId, targetId: task.OwnerId, targetType: task.OwnerKind);

        return new(ToInfo(task, snapshot));
    }

    public async Task<TaskResult> SetStatusAsync(
        Guid taskId, TaskItemStatus status, string? blockedNote, Guid actorUserId, CancellationToken ct = default)
    {
        var snapshot = await LoadAsync(ct);
        if (!snapshot.Tasks.TryGetValue(taskId, out var task))
            return Refuse("Tasks_Refusal_NoTask", actorUserId, taskId);
        if (!Enum.IsDefined(status))
            return Refuse("Tasks_Refusal_NoTask", actorUserId, taskId);

        var note = status == TaskItemStatus.Blocked ? blockedNote?.Trim() : null;
        if (status == TaskItemStatus.Blocked)
        {
            if (string.IsNullOrEmpty(note))
                return Refuse("Tasks_Refusal_NoteNeeded", actorUserId, taskId);
            if (note.Length > NoteMaxLength)
                return Refuse("Tasks_Refusal_NoteTooLong", actorUserId, taskId);
        }

        var before = task.Status;
        task.Status = status;
        task.BlockedNote = note;
        task.UpdatedAt = clock.GetCurrentInstant();
        await repository.UpdateAsync(task, ct);

        await audit.LogAsync(TasksAuditActions.TaskStatusChanged, AuditSubjectType, task.Id,
            $"Task '{task.Title}' on {TeamName(task, snapshot)} changed from {before} to {status}{(note is null ? string.Empty : $": {note}")}",
            actorUserId, targetId: task.OwnerId, targetType: task.OwnerKind);

        return new(ToInfo(task, snapshot));
    }

    public async Task<TaskResult> DeleteAsync(Guid taskId, Guid actorUserId, CancellationToken ct = default)
    {
        var snapshot = await LoadAsync(ct);
        if (!snapshot.Tasks.TryGetValue(taskId, out var task))
            return Refuse("Tasks_Refusal_NoTask", actorUserId, taskId);

        var info = ToInfo(task, snapshot);
        if (!await repository.DeleteAsync(taskId, ct))
            return Refuse("Tasks_Refusal_NoTask", actorUserId, taskId);

        await audit.LogAsync(TasksAuditActions.TaskDeleted, AuditSubjectType, task.Id,
            $"Task '{task.Title}' on {info.TeamName} deleted with {task.Assignments.Count} person(s) and {task.Dependencies.Count} link(s)",
            actorUserId, targetId: task.OwnerId, targetType: task.OwnerKind);

        return new(Removed: info);
    }

    public async Task<TaskResult> AssignAsync(Guid taskId, Guid userId, Guid actorUserId, CancellationToken ct = default)
    {
        var snapshot = await LoadAsync(ct);
        if (!snapshot.Tasks.TryGetValue(taskId, out var task))
            return Refuse("Tasks_Refusal_NoTask", actorUserId, taskId);
        var person = await users.GetUserInfoAsync(userId, ct);
        if (person is null || person.IsTombstone)
            return Refuse("Tasks_Refusal_NoPerson", actorUserId, taskId);
        userId = person.Id;
        if (task.Assignments.Any(a => a.UserId == userId))
            return Refuse("Tasks_Refusal_AlreadyOn", actorUserId, taskId);

        var assignment = new TaskAssignment
        {
            Id = Guid.NewGuid(),
            TaskItemId = task.Id,
            UserId = userId,
            State = TaskAssignmentState.Confirmed,
            CreatedAt = clock.GetCurrentInstant(),
        };
        if (!await repository.AddAssignmentAsync(assignment, ct))
            return Refuse("Tasks_Refusal_AlreadyOn", actorUserId, taskId);
        task.Assignments.Add(assignment);

        await audit.LogAsync(TasksAuditActions.TaskAssigned, AuditSubjectType, task.Id,
            $"Assigned to task '{task.Title}' on {TeamName(task, snapshot)}",
            actorUserId, targetId: userId, targetType: AuditSubjectTypes.User);

        return new(ToInfo(task, snapshot));
    }

    public async Task<TaskResult> UnassignAsync(Guid taskId, Guid userId, Guid actorUserId, CancellationToken ct = default)
    {
        var snapshot = await LoadAsync(ct);
        if (!snapshot.Tasks.TryGetValue(taskId, out var task))
            return Refuse("Tasks_Refusal_NoTask", actorUserId, taskId);

        var existing = task.Assignments.FirstOrDefault(a => a.UserId == userId);
        if (existing is null || !await repository.RemoveAssignmentAsync(taskId, userId, ct))
            return Refuse("Tasks_Refusal_NotOn", actorUserId, taskId);
        task.Assignments.Remove(existing);

        await audit.LogAsync(TasksAuditActions.TaskUnassigned, AuditSubjectType, task.Id,
            $"Unassigned from task '{task.Title}' on {TeamName(task, snapshot)}",
            actorUserId, targetId: userId, targetType: AuditSubjectTypes.User);

        return new(ToInfo(task, snapshot));
    }

    public async Task<TaskResult> AddDependencyAsync(Guid taskId, Guid dependsOnTaskId, Guid actorUserId, CancellationToken ct = default)
    {
        var snapshot = await LoadAsync(ct);
        if (!snapshot.Tasks.TryGetValue(taskId, out var task) || !snapshot.Tasks.TryGetValue(dependsOnTaskId, out var other))
            return Refuse("Tasks_Refusal_NoTask", actorUserId, taskId);
        if (taskId == dependsOnTaskId)
            return Refuse("Tasks_Refusal_DependsOnSelf", actorUserId, taskId);
        if (task.Dependencies.Any(d => d.DependsOnTaskItemId == dependsOnTaskId))
            return Refuse("Tasks_Refusal_DependencyExists", actorUserId, taskId);
        if (Reaches(snapshot.Tasks, from: dependsOnTaskId, to: taskId))
            return Refuse("Tasks_Refusal_DependencyCycle", actorUserId, taskId);

        var dependency = new TaskDependency { Id = Guid.NewGuid(), TaskItemId = taskId, DependsOnTaskItemId = dependsOnTaskId };
        if (!await repository.AddDependencyAsync(dependency, ct))
            return Refuse("Tasks_Refusal_DependencyExists", actorUserId, taskId);
        task.Dependencies.Add(dependency);

        await audit.LogAsync(TasksAuditActions.TaskDependencyAdded, AuditSubjectType, task.Id,
            $"Task '{task.Title}' now waits on '{other.Title}'",
            actorUserId, targetId: other.Id, targetType: AuditSubjectType);

        return new(ToInfo(task, snapshot));
    }

    public async Task<TaskResult> RemoveDependencyAsync(Guid taskId, Guid dependsOnTaskId, Guid actorUserId, CancellationToken ct = default)
    {
        var snapshot = await LoadAsync(ct);
        if (!snapshot.Tasks.TryGetValue(taskId, out var task))
            return Refuse("Tasks_Refusal_NoTask", actorUserId, taskId);

        var dependency = task.Dependencies.FirstOrDefault(d => d.DependsOnTaskItemId == dependsOnTaskId);
        if (dependency is null || !await repository.RemoveDependencyAsync(taskId, dependsOnTaskId, ct))
            return Refuse("Tasks_Refusal_NoDependency", actorUserId, taskId);
        task.Dependencies.Remove(dependency);

        var otherTitle = snapshot.Tasks.TryGetValue(dependsOnTaskId, out var other) ? other.Title : dependsOnTaskId.ToString();
        await audit.LogAsync(TasksAuditActions.TaskDependencyRemoved, AuditSubjectType, task.Id,
            $"Task '{task.Title}' no longer waits on '{otherTitle}'",
            actorUserId, targetId: dependsOnTaskId, targetType: AuditSubjectType);

        return new(ToInfo(task, snapshot));
    }

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var held = (await repository.GetAllAsync(ct))
            .SelectMany(t => t.Assignments.Where(a => a.UserId == userId).Select(a => (Task: t, Assignment: a)))
            .OrderBy(x => x.Assignment.CreatedAt)
            .ToList();

        return
        [
            new UserDataSlice(ExportSection, held.Select(x => new
            {
                Task = x.Task.Title,
                State = x.Assignment.State.ToString(),
                Since = x.Assignment.CreatedAt.ToIso8601(),
            }).ToList()),
        ];
    }

    /// <summary>Assignments and requests go. Free text on a task is not tied to a person and stays.</summary>
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        repository.RemoveUserAssignmentsAsync(userId, ct);

    public async Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
    {
        var moved = await repository.ReassignUserAsync(mergedFromUserId, mergedToUserId, ct);
        if (moved == 0)
            return;

        await audit.LogAsync(TasksAuditActions.TaskAssigned, AuditSubjectTypes.User, mergedToUserId,
            $"Took over {moved} task place(s) from merged account {mergedFromUserId}",
            actorUserId, targetId: mergedFromUserId, targetType: AuditSubjectTypes.User);
    }

    private sealed record Snapshot(
        Dictionary<Guid, TaskItem> Tasks,
        IReadOnlyDictionary<Guid, TeamInfo> Teams,
        IReadOnlyDictionary<Guid, EventSettingsInfo> Events);

    private sealed record StoredDate(LocalDate? Fixed, int? DayOffset);

    private async Task<Snapshot> LoadAsync(CancellationToken ct) => new(
        (await repository.GetAllAsync(ct)).ToDictionary(t => t.Id),
        await teams.GetTeamsAsync(ct),
        (await settings.GetAllEventSettingsAsync(ct)).ToDictionary(e => e.Id));

    private async Task<string?> TeamRefusalAsync(Guid teamId, CancellationToken ct)
    {
        var team = await teams.GetTeamAsync(teamId, ct);
        return team is { IsActive: true, IsSystemTeam: false } ? null : "Tasks_Refusal_NoTeam";
    }

    private string? InputRefusal(TaskInput input) => InputRefusals(input).Select(r => r.Key).FirstOrDefault();

    /// <summary>Every rule the input breaks, in the order they are checked. Field names are the bulk payload's.</summary>
    private IEnumerable<(string Field, string Key)> InputRefusals(TaskInput input)
    {
        var title = input.Title?.Trim();
        if (string.IsNullOrEmpty(title))
            yield return ("title", "Tasks_Refusal_NoTitle");
        else if (title.Length > TitleMaxLength)
            yield return ("title", "Tasks_Refusal_TitleTooLong");
        if (input.Description is { Length: > DescriptionMaxLength })
            yield return ("description", "Tasks_Refusal_DescriptionTooLong");
        if (input.SlotCount < 1)
            yield return ("slotCount", "Tasks_Refusal_NoSlots");
        if (input.ApprovalReason is { } reason && input.NeedsApproval && reason.Trim().Length > ReasonMaxLength)
            yield return ("approvalReason", "Tasks_Refusal_ReasonTooLong");
    }

    /// <summary>An event is picked only when new or changed: a task keeps an event that has since been withdrawn.</summary>
    private string? EventRefusal(Guid? eventId, Guid? currentEventId, Snapshot snapshot)
    {
        if (eventId is null || eventId == currentEventId)
            return null;
        return snapshot.Events.TryGetValue(eventId.Value, out var e) && e.Status != EventSettingsStatus.Deleted
            ? null
            : "Tasks_Refusal_NoEvent";
    }

    private static EventSettingsInfo? EventOf(Guid? eventId, Snapshot snapshot) =>
        eventId is { } id && snapshot.Events.TryGetValue(id, out var e) ? e : null;

    private (StoredDate Date, string? Refusal) ResolveDate(
        TaskDateInput? input, int? currentOffset, EventSettingsInfo? oldEvent, EventSettingsInfo? newEvent)
    {
        if (input is null)
            return (new StoredDate(null, null), null);
        if (!input.IsRelative)
            return (new StoredDate(input.Date, null), null);
        if (newEvent is null)
            return (new StoredDate(null, null), "Tasks_Refusal_EventNeededForRelative");

        // The form shows the date as the old event resolves it; posting it back unchanged keeps the day number.
        if (currentOffset is { } offset && oldEvent is not null && oldEvent.GateOpeningDate.PlusDays(offset) == input.Date)
            return (new StoredDate(null, offset), null);

        return (new StoredDate(null, Period.Between(newEvent.GateOpeningDate, input.Date, PeriodUnits.Days).Days), null);
    }

    private string? OrderRefusal(StoredDate start, StoredDate due, EventSettingsInfo? newEvent)
    {
        var from = Resolve(start, newEvent);
        var to = Resolve(due, newEvent);
        return from is { } f && to is { } t && t < f ? "Tasks_Refusal_DueBeforeStart" : null;
    }

    private static LocalDate? Resolve(StoredDate date, EventSettingsInfo? taskEvent) =>
        date.Fixed ?? (date.DayOffset is { } offset && taskEvent is not null ? taskEvent.GateOpeningDate.PlusDays(offset) : null);

    private static void Apply(TaskItem task, TaskInput input, StoredDate start, StoredDate due, Instant now)
    {
        task.OwnerKind = TeamOwner.Kind;
        task.OwnerId = input.TeamId;
        task.Title = input.Title.Trim();
        task.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description;
        task.EventId = input.EventId;
        task.StartDate = start.Fixed;
        task.StartDayOffset = start.DayOffset;
        task.DueDate = due.Fixed;
        task.DueDayOffset = due.DayOffset;
        task.Priority = input.Priority;
        task.SlotCount = input.SlotCount;
        task.NeedsApproval = input.NeedsApproval;
        task.ApprovalReason = ApprovalReasonOf(input);
        task.IsHidden = input.IsHidden;
        task.UpdatedAt = now;
    }

    private static string? ApprovalReasonOf(TaskInput input) =>
        input.NeedsApproval && !string.IsNullOrWhiteSpace(input.ApprovalReason) ? input.ApprovalReason.Trim() : null;

    private static List<string> Changes(TaskItem task, TaskInput input, StoredDate start, StoredDate due)
    {
        var changes = new List<string>();
        void Check(bool changed, string name)
        {
            if (changed)
                changes.Add(name);
        }

        Check(task.OwnerId != input.TeamId, "team");
        Check(!string.Equals(task.Title, input.Title.Trim(), StringComparison.Ordinal), "title");
        Check(!string.Equals(task.Description ?? string.Empty, input.Description ?? string.Empty, StringComparison.Ordinal), "description");
        Check(task.EventId != input.EventId, "event");
        Check(task.StartDate != start.Fixed || task.StartDayOffset != start.DayOffset, "start");
        Check(task.DueDate != due.Fixed || task.DueDayOffset != due.DayOffset, "due");
        Check(task.Priority != input.Priority, "priority");
        Check(task.SlotCount != input.SlotCount, "slots");
        Check(task.NeedsApproval != input.NeedsApproval, "needs approval");
        Check(!string.Equals(task.ApprovalReason, ApprovalReasonOf(input), StringComparison.Ordinal), "approval reason");
        Check(task.IsHidden != input.IsHidden, "hidden");
        return changes;
    }

    private static string TeamName(TaskItem task, Snapshot snapshot) =>
        snapshot.Teams.TryGetValue(task.OwnerId, out var team) ? team.Name : $"{task.OwnerKind} {task.OwnerId}";

    private static bool Reaches(IReadOnlyDictionary<Guid, TaskItem> tasks, Guid from, Guid to)
    {
        var seen = new HashSet<Guid>();
        var stack = new Stack<Guid>([from]);
        while (stack.TryPop(out var id))
        {
            if (id == to)
                return true;
            if (!seen.Add(id) || !tasks.TryGetValue(id, out var task))
                continue;
            foreach (var dependency in task.Dependencies)
                stack.Push(dependency.DependsOnTaskItemId);
        }

        return false;
    }

    private static TaskInfo ToInfo(TaskItem task, Snapshot snapshot)
    {
        var team = snapshot.Teams.GetValueOrDefault(task.OwnerId);
        var taskEvent = EventOf(task.EventId, snapshot);
        return new TaskInfo(
            task.Id,
            new Owner(task.OwnerKind, task.OwnerId),
            team?.Name ?? string.Empty,
            team?.Slug ?? string.Empty,
            team?.IsHidden ?? false,
            task.Title,
            task.Description,
            task.EventId,
            taskEvent?.EventName,
            DateInfo(task.StartDate, task.StartDayOffset, taskEvent),
            DateInfo(task.DueDate, task.DueDayOffset, taskEvent),
            task.Status,
            task.BlockedNote,
            task.Priority,
            task.SlotCount,
            task.NeedsApproval,
            task.ApprovalReason,
            task.IsHidden,
            task.Assignments
                .OrderBy(a => a.CreatedAt)
                .Select(a => new TaskAssignmentInfo(a.UserId, a.State == TaskAssignmentState.Confirmed, a.CreatedAt))
                .ToList(),
            task.Dependencies
                .Select(d => snapshot.Tasks.GetValueOrDefault(d.DependsOnTaskItemId))
                .OfType<TaskItem>()
                .Select(o => new TaskLinkInfo(o.Id, o.Title, o.Status))
                .ToList(),
            task.CreatedAt,
            task.UpdatedAt);
    }

    private static TaskDateInfo? DateInfo(LocalDate? fixedDate, int? offset, EventSettingsInfo? taskEvent) =>
        fixedDate is null && offset is null
            ? null
            : new TaskDateInfo(fixedDate, offset, Resolve(new StoredDate(fixedDate, offset), taskEvent));

    /// <summary>Logs the refusal by its resource key and answers it in the current UI culture.</summary>
    private TaskResult Refuse(string key, Guid actorUserId, Guid? taskId = null)
    {
        logger.LogWarning("Task write refused ({Refusal}) for {ActorUserId} on task {TaskId}", key, actorUserId, taskId);
        return new(Refusal: localizer[key].Value);
    }
}
