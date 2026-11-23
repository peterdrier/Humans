using Humans.Settings.Contracts;
using Humans.Tasks.Contracts;
using Humans.Tasks.Data;
using Humans.Tasks.Domain;
using Humans.Teams.Contracts;
using NodaTime;

namespace Humans.Tasks.Services;

// Bulk load: the whole payload is planned against the same rules a single create uses, then written in one go.
internal sealed partial class TaskService
{
    /// <summary>A task of the payload that passed every check on its own: ready to write.</summary>
    private sealed record BulkPlan(BulkLoadTask Source, string Key, TaskInput Input, StoredDate Start, StoredDate Due);

    public async Task<BulkLoadResult> BulkLoadAsync(BulkLoadRequest request, Guid actorUserId, CancellationToken ct = default)
    {
        var snapshot = await LoadAsync(ct);
        var problems = new List<BulkLoadProblem>();
        var plans = new List<BulkPlan?>();

        if (request.Tasks.Count == 0)
            problems.Add(new(null, null, "tasks", "Send at least one task."));

        var teamsByRef = new Dictionary<string, TeamInfo?>(StringComparer.Ordinal);
        var firstIndexOfKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < request.Tasks.Count; i++)
        {
            var plan = await PlanAsync(i, request.Tasks[i], snapshot, teamsByRef, firstIndexOfKey, problems, ct);
            plans.Add(plan);
        }

        CheckDependencies(request.Tasks, firstIndexOfKey, problems);

        if (problems.Count > 0)
        {
            logger.LogWarning("Bulk task load refused with {Problems} problem(s) for {ActorUserId}", problems.Count, actorUserId);
            return new([.. problems], [], request.DryRun);
        }

        var ready = plans.OfType<BulkPlan>().ToList();
        if (request.DryRun)
            return new([], [.. ready.Select(p => new BulkLoadedTask(p.Key, null))], DryRun: true);

        var now = clock.GetCurrentInstant();
        var created = ready.ToDictionary(
            p => p.Key,
            p =>
            {
                var task = new TaskItem { Id = Guid.NewGuid(), OwnerKind = TeamOwner.Kind, CreatedAt = now };
                Apply(task, p.Input, p.Start, p.Due, now);
                return task;
            },
            StringComparer.Ordinal);
        var links = ready
            .SelectMany(p => DependencyKeys(p.Source).Select(key => new TaskDependency
            {
                Id = Guid.NewGuid(),
                TaskItemId = created[p.Key].Id,
                DependsOnTaskItemId = created[key].Id,
            }))
            .ToList();
        await repository.AddManyAsync([.. created.Values], links, ct);

        foreach (var task in created.Values)
        {
            await audit.LogAsync(TasksAuditActions.TaskCreated, AuditEntityType, task.Id,
                $"Task '{task.Title}' created on {TeamName(task, snapshot)} by bulk load",
                actorUserId, relatedEntityId: task.OwnerId, relatedEntityType: task.OwnerKind);
        }

        foreach (var plan in ready)
        {
            foreach (var key in DependencyKeys(plan.Source))
            {
                await audit.LogAsync(TasksAuditActions.TaskDependencyAdded, AuditEntityType, created[plan.Key].Id,
                    $"Task '{created[plan.Key].Title}' now waits on '{created[key].Title}'",
                    actorUserId, relatedEntityId: created[key].Id, relatedEntityType: AuditEntityType);
            }
        }

        return new([], [.. ready.Select(p => new BulkLoadedTask(p.Key, created[p.Key].Id))], DryRun: false);
    }

    /// <summary>Null when anything about the task is wrong; every such thing is added to <paramref name="problems"/>.</summary>
    private async Task<BulkPlan?> PlanAsync(
        int index,
        BulkLoadTask task,
        Snapshot snapshot,
        Dictionary<string, TeamInfo?> teamsByRef,
        Dictionary<string, int> firstIndexOfKey,
        List<BulkLoadProblem> problems,
        CancellationToken ct)
    {
        var key = task.Key?.Trim();
        var found = 0;

        void Problem(string field, string message)
        {
            found++;
            problems.Add(new(index, key, field, message));
        }

        void Refused(string field, string resourceKey) => Problem(field, localizer[resourceKey].Value);

        if (string.IsNullOrEmpty(key))
            Problem("key", "Give the task a key, to name it in the payload.");
        else if (firstIndexOfKey.TryGetValue(key, out var first))
            Problem("key", $"The key '{key}' is already used by task {first}.");
        else
            firstIndexOfKey[key] = index;

        Guid? teamId = null;
        if (string.IsNullOrWhiteSpace(task.Team))
        {
            Problem("team", "Name the team by slug or id.");
        }
        else
        {
            var reference = task.Team.Trim();
            if (!teamsByRef.TryGetValue(reference, out var team))
            {
                team = await teams.GetTeamBySlugAsync(reference, ct)
                    ?? (Guid.TryParse(reference, out var id) ? await teams.GetTeamAsync(id, ct) : null);
                teamsByRef[reference] = team;
            }

            if (team is null)
                Problem("team", $"No team '{reference}'.");
            else if (team is not { IsActive: true, IsSystemTeam: false })
                Refused("team", "Tasks_Refusal_NoTeam");
            else
                teamId = team.Id;
        }

        var input = new TaskInput(
            teamId ?? Guid.Empty, task.Title, task.Description, task.EventId, null, null,
            task.Priority, task.SlotCount, task.NeedsApproval, task.ApprovalReason, task.IsHidden);
        foreach (var (field, resourceKey) in InputRefusals(input))
            Refused(field, resourceKey);

        if (!Enum.IsDefined(task.Priority))
            Problem("priority", "That priority does not exist.");

        var eventRefusal = EventRefusal(task.EventId, currentEventId: null, snapshot);
        if (eventRefusal is not null)
            Refused("eventId", eventRefusal);

        var taskEvent = EventOf(task.EventId, snapshot);
        var start = BulkDate("start", task.Start, task.EventId is not null, taskEvent, Problem, Refused);
        var due = BulkDate("due", task.Due, task.EventId is not null, taskEvent, Problem, Refused);
        if (start is not null && due is not null && OrderRefusal(start, due, taskEvent) is { } orderRefusal)
            Refused("due", orderRefusal);

        return found == 0 && key is not null && start is not null && due is not null
            ? new BulkPlan(task, key, input, start, due)
            : null;
    }

    /// <summary>Null when the date is wrong, after reporting why. An absent date is stored as none.</summary>
    private StoredDate? BulkDate(
        string field,
        BulkLoadDate? date,
        bool hasEventId,
        EventSettingsInfo? taskEvent,
        Action<string, string> problem,
        Action<string, string> refused)
    {
        if (date is null)
            return new StoredDate(null, null);
        if (new object?[] { date.Fixed, date.Day, date.RelativeDate }.Count(x => x is not null) != 1)
        {
            problem(field, "Give exactly one of fixed, day or relativeDate.");
            return null;
        }

        if (date.Fixed is { } fixedDate)
            return new StoredDate(fixedDate, null);

        if (taskEvent is null)
        {
            // An event id that matches nothing has already been reported; only a task with no event is reported here.
            if (!hasEventId)
                refused(field, "Tasks_Refusal_EventNeededForRelative");
            return null;
        }

        if (date.Day is { } day)
            return new StoredDate(null, day);

        var (resolved, refusal) = ResolveDate(new TaskDateInput(IsRelative: true, date.RelativeDate!.Value), null, null, taskEvent);
        if (refusal is not null)
        {
            refused(field, refusal);
            return null;
        }

        return resolved;
    }

    /// <summary>The payload keys a task names as what it waits on, each once.</summary>
    private static IEnumerable<string> DependencyKeys(BulkLoadTask task) =>
        (task.DependsOn ?? []).Select(k => k?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal);

    /// <summary>Unknown keys, a task waiting on itself, and circles.</summary>
    private void CheckDependencies(
        IReadOnlyList<BulkLoadTask> tasks, Dictionary<string, int> firstIndexOfKey, List<BulkLoadProblem> problems)
    {
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var i = 0; i < tasks.Count; i++)
        {
            var key = tasks[i].Key?.Trim();
            if (string.IsNullOrEmpty(key) || firstIndexOfKey.GetValueOrDefault(key, -1) != i)
                continue;

            var waitsOn = new List<string>();
            foreach (var dependency in DependencyKeys(tasks[i]))
            {
                if (string.Equals(dependency, key, StringComparison.Ordinal))
                    problems.Add(new(i, key, "dependsOn", localizer["Tasks_Refusal_DependsOnSelf"].Value));
                else if (!firstIndexOfKey.ContainsKey(dependency))
                    problems.Add(new(i, key, "dependsOn", $"'{dependency}' is not a task key in this payload."));
                else
                    waitsOn.Add(dependency);
            }

            edges[key] = waitsOn;
        }

        var reported = new HashSet<string>(StringComparer.Ordinal);
        var finished = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();

        void Visit(string key)
        {
            path.Add(key);
            foreach (var next in edges[key])
            {
                var at = path.IndexOf(next);
                if (at >= 0)
                {
                    var circle = path.Skip(at).ToList();
                    if (reported.Add(string.Join('\n', circle.Order(StringComparer.Ordinal))))
                    {
                        problems.Add(new(firstIndexOfKey[circle[0]], circle[0], "dependsOn",
                            $"These tasks wait on each other in a circle: {string.Join(" -> ", circle.Append(next))}."));
                    }
                }
                else if (!finished.Contains(next))
                {
                    Visit(next);
                }
            }

            path.RemoveAt(path.Count - 1);
            finished.Add(key);
        }

        foreach (var key in edges.Keys.Where(k => !finished.Contains(k)))
            Visit(key);
    }
}
