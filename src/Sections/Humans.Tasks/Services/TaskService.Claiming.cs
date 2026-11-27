using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Tasks.Domain;

namespace Humans.Tasks.Services;

// Claiming and requests. A claim never checks permissions (controllers do) and never adds team membership.
[Humans.Base.Attributes.Grandfathered("HUM0046", "Fold after the in-flight Tasks work lands.", "2026-11-27", "peterdrier/Humans#2675")]
internal sealed partial class TaskService
{
    public async Task<TaskResult> ClaimAsync(Guid taskId, Guid actorUserId, CancellationToken ct = default)
    {
        var snapshot = await LoadAsync(ct);
        if (!snapshot.Tasks.TryGetValue(taskId, out var task))
            return Refuse("Tasks_Refusal_NoTask", actorUserId, taskId);
        if (task.Status is TaskItemStatus.Done or TaskItemStatus.Cancelled)
            return Refuse("Tasks_Refusal_Finished", actorUserId, taskId);
        if (task.Assignments.FirstOrDefault(a => a.UserId == actorUserId) is { } held)
            return Refuse(held.State == TaskAssignmentState.Confirmed ? "Tasks_Refusal_YouAreOn" : "Tasks_Refusal_YouRequested", actorUserId, taskId);
        if (task.Assignments.Count(a => a.State == TaskAssignmentState.Confirmed) >= task.SlotCount)
            return Refuse("Tasks_Refusal_Full", actorUserId, taskId);

        // The slot check above is the only guard: two claims at the same moment may both land, which shows as 5 of 4.
        var state = task.NeedsApproval ? TaskAssignmentState.Requested : TaskAssignmentState.Confirmed;
        var assignment = new TaskAssignment
        {
            Id = Guid.NewGuid(),
            TaskItemId = task.Id,
            UserId = actorUserId,
            State = state,
            CreatedAt = clock.GetCurrentInstant(),
        };
        if (!await repository.AddAssignmentAsync(assignment, ct))
            return Refuse("Tasks_Refusal_YouAreOn", actorUserId, taskId);
        task.Assignments.Add(assignment);

        var (action, text) = state == TaskAssignmentState.Confirmed
            ? (TasksAuditActions.TaskClaimed, "Took a place on")
            : (TasksAuditActions.TaskRequested, "Asked to join");
        await audit.LogAsync(action, AuditEntityType, task.Id,
            $"{text} task '{task.Title}' on {TeamName(task, snapshot)}",
            actorUserId, relatedEntityId: actorUserId, relatedEntityType: AuditSubjectTypes.User);

        return new(ToInfo(task, snapshot));
    }

    public async Task<TaskResult> ReleaseAsync(Guid taskId, Guid actorUserId, CancellationToken ct = default)
    {
        var snapshot = await LoadAsync(ct);
        if (!snapshot.Tasks.TryGetValue(taskId, out var task))
            return Refuse("Tasks_Refusal_NoTask", actorUserId, taskId);

        var held = task.Assignments.FirstOrDefault(a => a.UserId == actorUserId && a.State == TaskAssignmentState.Confirmed);
        if (held is null || !await repository.RemoveAssignmentAsync(taskId, actorUserId, ct))
            return Refuse("Tasks_Refusal_YouAreNotOn", actorUserId, taskId);
        task.Assignments.Remove(held);

        await audit.LogAsync(TasksAuditActions.TaskReleased, AuditEntityType, task.Id,
            $"Gave back their place on task '{task.Title}' on {TeamName(task, snapshot)}",
            actorUserId, relatedEntityId: actorUserId, relatedEntityType: AuditSubjectTypes.User);

        return new(ToInfo(task, snapshot));
    }

    public async Task<TaskResult> WithdrawRequestAsync(Guid taskId, Guid actorUserId, CancellationToken ct = default)
    {
        var snapshot = await LoadAsync(ct);
        if (!snapshot.Tasks.TryGetValue(taskId, out var task))
            return Refuse("Tasks_Refusal_NoTask", actorUserId, taskId);

        var request = RequestOf(task, actorUserId);
        if (request is null || !await repository.RemoveRequestAsync(taskId, actorUserId, ct))
            return Refuse("Tasks_Refusal_NoYourRequest", actorUserId, taskId);
        task.Assignments.Remove(request);

        await audit.LogAsync(TasksAuditActions.TaskRequestWithdrawn, AuditEntityType, task.Id,
            $"Took back their request to join task '{task.Title}' on {TeamName(task, snapshot)}",
            actorUserId, relatedEntityId: actorUserId, relatedEntityType: AuditSubjectTypes.User);

        return new(ToInfo(task, snapshot));
    }

    public async Task<TaskResult> ApproveRequestAsync(Guid taskId, Guid userId, Guid actorUserId, CancellationToken ct = default)
    {
        var snapshot = await LoadAsync(ct);
        if (!snapshot.Tasks.TryGetValue(taskId, out var task))
            return Refuse("Tasks_Refusal_NoTask", actorUserId, taskId);

        var request = RequestOf(task, userId);
        if (request is null || !await repository.ConfirmRequestAsync(taskId, userId, ct))
            return Refuse("Tasks_Refusal_NoRequest", actorUserId, taskId);
        request.State = TaskAssignmentState.Confirmed;

        // A task that filled up meanwhile is overfilled, not refused.
        var confirmed = task.Assignments.Count(a => a.State == TaskAssignmentState.Confirmed);
        await audit.LogAsync(TasksAuditActions.TaskRequestApproved, AuditEntityType, task.Id,
            $"Approved a request to join task '{task.Title}' on {TeamName(task, snapshot)} ({confirmed} of {task.SlotCount})",
            actorUserId, relatedEntityId: userId, relatedEntityType: AuditSubjectTypes.User);

        await SendAnswerAsync(approved: true, task, snapshot, userId, reason: null, ct);
        return new(ToInfo(task, snapshot));
    }

    /// <summary>The optional reason goes only to the email and the audit entry: it is not stored on the task.</summary>
    public async Task<TaskResult> DeclineRequestAsync(
        Guid taskId, Guid userId, string? reason, Guid actorUserId, CancellationToken ct = default)
    {
        var snapshot = await LoadAsync(ct);
        if (!snapshot.Tasks.TryGetValue(taskId, out var task))
            return Refuse("Tasks_Refusal_NoTask", actorUserId, taskId);

        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (reason is { Length: > ReasonMaxLength })
            return Refuse("Tasks_Refusal_ReasonTooLong", actorUserId, taskId);

        var request = RequestOf(task, userId);
        if (request is null || !await repository.RemoveRequestAsync(taskId, userId, ct))
            return Refuse("Tasks_Refusal_NoRequest", actorUserId, taskId);
        task.Assignments.Remove(request);

        await audit.LogAsync(TasksAuditActions.TaskRequestDeclined, AuditEntityType, task.Id,
            $"Declined a request to join task '{task.Title}' on {TeamName(task, snapshot)}{(reason is null ? string.Empty : $": {reason}")}",
            actorUserId, relatedEntityId: userId, relatedEntityType: AuditSubjectTypes.User);

        await SendAnswerAsync(approved: false, task, snapshot, userId, reason, ct);
        return new(ToInfo(task, snapshot));
    }

    private static TaskAssignment? RequestOf(TaskItem task, Guid userId) =>
        task.Assignments.FirstOrDefault(a => a.UserId == userId && a.State == TaskAssignmentState.Requested);

    /// <summary>The answer is already saved and audited: a failed email is logged, never undoes it.</summary>
    private async Task SendAnswerAsync(
        bool approved, TaskItem task, Snapshot snapshot, Guid userId, string? reason, CancellationToken ct)
    {
        try
        {
            var person = await users.GetUserInfoAsync(userId, ct)
                ?? throw new InvalidOperationException($"No user {userId} for a request on task {task.Id}");

            var request = new TaskAnswerEmail(
                person.Id, person.Email!, person.BurnerName, task.Id, task.Title, TeamName(task, snapshot), reason,
                person.PreferredLanguage.IsSupportedCultureCode() ? person.PreferredLanguage : CultureCatalog.DefaultCultureCode);
            await email.SendAsync(approved ? emails.RequestApproved(request) : emails.RequestDeclined(request), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Failed to queue the task request answer email for {UserId} on task {TaskId}", userId, task.Id);
        }
    }
}
