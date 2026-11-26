using Humans.Base.Controllers;
using Humans.Permissions.Contracts;
using Humans.Settings.Contracts;
using Humans.Tasks.Authorization;
using Humans.Tasks.Domain;
using Humans.Tasks.Models;
using Humans.Tasks.Services;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace Humans.Tasks.Controllers;

[Authorize]
[Route("Tasks")]
internal sealed class TasksController(
    ITaskService tasks,
    IPermissionService permissions,
    ITeamService teams,
    ISettingsService settings,
    IUserService userService,
    IAuthorizationService authorization,
    IStringLocalizer<TasksResource> localizer,
    ILogger<TasksController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] TaskListFilter filter, CancellationToken ct)
    {
        if (await AccessAsync(ct) is not { } access) return Unauthorized();

        var visible = (await tasks.GetListedTasksAsync(filter.ToScope(), ct)).Where(access.CanSee).OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        return View(new TaskListViewModel(
            visible, await EventsAsync(currentEventId: null, ct), filter, await ManagedTeamsAsync(access, currentTeamId: null, ct), access.PlacesOn(visible)));
    }

    [HttpGet("Open")]
    public async Task<IActionResult> Open(CancellationToken ct)
    {
        if (await AccessAsync(ct) is not { } access) return Unauthorized();

        var open = (await tasks.GetListedTasksAsync(scope: null, ct))
            .Where(t => access.CanSee(t) && !t.IsFinished && t.OpenSlots > 0)
            .OrderBy(t => TaskPriorityOrder.Rank(t.Priority))
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return View(new OpenBoardViewModel(
            open, open.Sum(t => t.OpenSlots), open.Count(t => t.Priority == SlotPriority.Critical), access.PlacesOn(open)));
    }

    [HttpGet("Team/{slug}")]
    public async Task<IActionResult> Team(string slug, [FromQuery] TaskListFilter filter, CancellationToken ct)
    {
        if (await AccessAsync(ct) is not { } access) return Unauthorized();
        if (await FindTeamAsync(slug, ct) is not { } team) return NotFound();

        var onTeam = (await tasks.GetTasksAsync(ct)).Where(t => t.Owner.Id == team.Id).ToList();
        if (!access.CanSeeTeam(team.Id, team.IsHidden, onTeam)) return NotFound();

        var addForm = new TaskForm { TeamId = team.Id, EventId = settings.GetActiveEventSettings()?.Id };
        return View(await TeamPageAsync(team, access, onTeam, filter, addForm, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        if (await AccessAsync(ct) is not { } access) return Unauthorized();
        if (await tasks.GetTaskAsync(id, ct) is not { } task || !access.CanSee(task)) return NotFound();

        return View(await TaskPageAsync(task, access, TaskForm.From(task), editOpen: false, ct));
    }

    [HttpPost("Team/{slug}/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(string slug, TaskForm form, CancellationToken ct)
    {
        if (await AccessAsync(ct) is not { } access) return Unauthorized();
        if (await FindTeamAsync(slug, ct) is not { } team) return NotFound();
        if (!access.Manages(team.Id) || form.TeamId != team.Id) return Forbid();

        var result = ModelState.IsValid ? await tasks.CreateAsync(form.ToInput(), access.UserId, ct) : Unreadable();
        if (!result.Succeeded)
        {
            SetError(result.Refusal!);
            var onTeam = (await tasks.GetTasksAsync(ct)).Where(t => t.Owner.Id == team.Id).ToList();
            return View(nameof(Team), await TeamPageAsync(team, access, onTeam, new TaskListFilter(), form, ct));
        }

        SetSuccess(localizer["Tasks_Added", result.Task!.Title].Value);
        return RedirectToAction(nameof(Details), new { id = result.Task.Id });
    }

    [HttpPost("{id:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, TaskForm form, CancellationToken ct)
    {
        if (await AccessAsync(ct) is not { } access) return Unauthorized();
        if (await tasks.GetTaskAsync(id, ct) is not { } task || !access.CanSee(task)) return NotFound();
        if (!access.Manages(task) || (form.TeamId != task.Owner.Id && !access.CanMove(task, form.TeamId))) return Forbid();

        var result = ModelState.IsValid ? await tasks.UpdateAsync(id, form.ToInput(), access.UserId, ct) : Unreadable();
        if (!result.Succeeded)
        {
            SetError(result.Refusal!);
            return View(nameof(Details), await TaskPageAsync(task, access, form, editOpen: true, ct));
        }

        SetSuccess(localizer["Tasks_Saved", result.Task!.Title].Value);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/Status")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(Guid id, StatusForm form, CancellationToken ct)
    {
        if (await AccessAsync(ct) is not { } access) return Unauthorized();
        if (await tasks.GetTaskAsync(id, ct) is not { } task || !access.CanSee(task)) return NotFound();
        if (!access.CanSetStatus(task, form.Status)) return Forbid();

        var result = ModelState.IsValid ? await tasks.SetStatusAsync(id, form.Status, form.BlockedNote, access.UserId, ct) : Unreadable();
        if (!result.Succeeded)
        {
            SetError(result.Refusal!);
            return View(nameof(Details), await TaskPageAsync(task, access, TaskForm.From(task), editOpen: false, ct, form));
        }

        SetSuccess(localizer["Tasks_StatusChanged"].Value);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/Assign")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Assign(Guid id, Guid userId, CancellationToken ct) =>
        await ChangeAsync(id, ct, (task, access) => access.Manages(task),
            (task, access) => tasks.AssignAsync(id, userId, access.UserId, ct), "Tasks_Assigned");

    [HttpPost("{id:guid}/Unassign")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unassign(Guid id, Guid userId, CancellationToken ct) =>
        await ChangeAsync(id, ct, (task, access) => access.Manages(task),
            (task, access) => tasks.UnassignAsync(id, userId, access.UserId, ct), "Tasks_Unassigned");

    [HttpPost("{id:guid}/Waits/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddDependency(Guid id, Guid dependsOn, CancellationToken ct) =>
        await ChangeAsync(id, ct, (task, access) => access.Manages(task), async (task, access) =>
            await tasks.GetTaskAsync(dependsOn, ct) is { } other && access.CanSee(other)
                ? await tasks.AddDependencyAsync(id, dependsOn, access.UserId, ct)
                : new TaskResult(Refusal: localizer["Tasks_Refusal_NoTask"].Value), "Tasks_DependencyAdded");

    [HttpPost("{id:guid}/Waits/Remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveDependency(Guid id, Guid dependsOn, CancellationToken ct) =>
        await ChangeAsync(id, ct, (task, access) => access.Manages(task),
            (task, access) => tasks.RemoveDependencyAsync(id, dependsOn, access.UserId, ct), "Tasks_DependencyRemoved");

    // Claim, release and withdraw only ever act on the signed-in person, so seeing the task is the whole check; the
    // service refuses a full, finished or already-held task in words, which a stale page needs more than a 403.
    [HttpPost("{id:guid}/Claim")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Claim(Guid id, string? returnUrl, CancellationToken ct) =>
        await ChangeAsync(id, ct, (_, _) => true, (task, access) => tasks.ClaimAsync(id, access.UserId, ct),
            task => task.NeedsApproval ? "Tasks_Requested" : "Tasks_Claimed", returnUrl);

    [HttpPost("{id:guid}/Release")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Release(Guid id, string? returnUrl, CancellationToken ct) =>
        await ChangeAsync(id, ct, (_, _) => true, (task, access) => tasks.ReleaseAsync(id, access.UserId, ct), "Tasks_Released", returnUrl);

    [HttpPost("{id:guid}/Withdraw")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(Guid id, string? returnUrl, CancellationToken ct) =>
        await ChangeAsync(id, ct, (_, _) => true, (task, access) => tasks.WithdrawRequestAsync(id, access.UserId, ct), "Tasks_Withdrawn", returnUrl);

    [HttpPost("{id:guid}/Approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(Guid id, Guid userId, string? returnUrl, CancellationToken ct) =>
        await ChangeAsync(id, ct, (task, access) => access.Manages(task),
            (task, access) => tasks.ApproveRequestAsync(id, userId, access.UserId, ct), "Tasks_Approved", returnUrl);

    [HttpPost("{id:guid}/Decline")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Decline(Guid id, Guid userId, string? reason, string? returnUrl, CancellationToken ct) =>
        await ChangeAsync(id, ct, (task, access) => access.Manages(task),
            (task, access) => tasks.DeclineRequestAsync(id, userId, reason, access.UserId, ct), "Tasks_Declined", returnUrl);

    [HttpPost("{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (await AccessAsync(ct) is not { } access) return Unauthorized();
        if (await tasks.GetTaskAsync(id, ct) is not { } task || !access.CanSee(task)) return NotFound();
        if (!access.Manages(task)) return Forbid();

        var result = await tasks.DeleteAsync(id, access.UserId, ct);
        if (!result.Succeeded)
        {
            SetError(result.Refusal!);
            return RedirectToAction(nameof(Details), new { id });
        }

        SetSuccess(localizer["Tasks_Deleted", task.Title].Value);
        return RedirectToAction(nameof(Team), new { slug = task.TeamSlug.Length > 0 ? task.TeamSlug : task.Owner.Id.ToString() });
    }

    /// <summary>The shape of every small write on a task page: find it, check who may, run it, say what happened.</summary>
    private Task<IActionResult> ChangeAsync(
        Guid id, CancellationToken ct,
        Func<TaskInfo, TasksAccess, bool> allowed,
        Func<TaskInfo, TasksAccess, Task<TaskResult>> change,
        string successKey,
        string? returnUrl = null) =>
        ChangeAsync(id, ct, allowed, change, _ => successKey, returnUrl);

    /// <summary>The message may take the task's title as <c>{0}</c>. A write posted from a list goes back to that list when <paramref name="returnUrl"/> is local.</summary>
    private async Task<IActionResult> ChangeAsync(
        Guid id, CancellationToken ct,
        Func<TaskInfo, TasksAccess, bool> allowed,
        Func<TaskInfo, TasksAccess, Task<TaskResult>> change,
        Func<TaskInfo, string> successKey,
        string? returnUrl = null)
    {
        if (await AccessAsync(ct) is not { } access) return Unauthorized();
        if (await tasks.GetTaskAsync(id, ct) is not { } task || !access.CanSee(task)) return NotFound();
        if (!allowed(task, access)) return Forbid();

        var result = await change(task, access);
        if (result.Succeeded) SetSuccess(localizer[successKey(task), task.Title].Value);
        else SetError(result.Refusal!);
        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl!) : RedirectToAction(nameof(Details), new { id });
    }

    private Task<TasksAccess?> AccessAsync(CancellationToken ct) => TasksAccess.ForAsync(User, permissions, authorization, ct);

    /// <summary>A field that did not bind (a malformed date or event id) would otherwise reach the service as "none".</summary>
    private TaskResult Unreadable()
    {
        var fields = string.Join(", ", ModelState
            .Where(e => e.Value?.Errors.Count > 0)
            .Select(e => e.Key[(e.Key.LastIndexOf('.') + 1)..] switch
            {
                nameof(TaskForm.StartDate) => localizer["Tasks_Form_Start"].Value,
                nameof(TaskForm.DueDate) => localizer["Tasks_Form_Due"].Value,
                nameof(TaskForm.EventId) => localizer["Tasks_Form_Event"].Value,
                nameof(TaskForm.TeamId) => localizer["Tasks_Form_Team"].Value,
                nameof(TaskForm.SlotCount) => localizer["Tasks_Form_Slots"].Value,
                nameof(TaskForm.Priority) => localizer["Tasks_Form_Priority"].Value,
                "Status" => localizer["Tasks_Col_Status"].Value,
                var field => field
            }));
        logger.LogWarning("Task form refused because these fields did not bind: {Fields}", fields);
        return new(Refusal: localizer["Tasks_Refusal_Unreadable", fields].Value);
    }

    private async Task<TeamPageViewModel> TeamPageAsync(
        TeamInfo team, TasksAccess access, IReadOnlyList<TaskInfo> onTeam, TaskListFilter filter, TaskForm addForm, CancellationToken ct)
    {
        var listed = (await tasks.GetListedTasksAsync(filter.ToScope(), ct))
            .Where(t => t.Owner.Id == team.Id && access.CanSee(t))
            .OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var canAdd = access.Manages(team.Id) && team.IsActive && !team.IsSystemTeam;
        return new TeamPageViewModel(
            team.Id, team.Name, team.Slug, listed, filter, canAdd, addForm, await EventsAsync(addForm.EventId, ct),
            access.PlacesOn(listed), access.Manages(team.Id) ? RequestsOn(onTeam) : []);
    }

    private async Task<TaskPageViewModel> TaskPageAsync(
        TaskInfo task, TasksAccess access, TaskForm editForm, bool editOpen, CancellationToken ct, StatusForm? statusForm = null)
    {
        var all = await tasks.GetTasksAsync(ct);
        var visibleById = all.Where(access.CanSee).ToDictionary(t => t.Id);
        var canManage = access.Manages(task);
        var waitingOn = task.WaitingOn
            .Select(l => visibleById.ContainsKey(l.Id) ? new DependencyRow(l.Id, l.Title, l.Status) : new DependencyRow(l.Id, null, null))
            .OrderBy(r => r.Title is null)
            .ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var choices = canManage
            ? visibleById.Values
                .Where(t => t.Id != task.Id && task.WaitingOn.All(l => l.Id != t.Id))
                .OrderBy(t => t.TeamName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(t => new DependencyChoice(t.Id, $"{t.TeamName}: {t.Title}"))
                .ToList()
            : [];
        var teamChoices = canManage ? await ManagedTeamsAsync(access, task.Owner.Id, ct) : [];

        return new TaskPageViewModel(
            task,
            canManage,
            [.. Enum.GetValues<TaskItemStatus>().Where(s => access.CanSetStatus(task, s))],
            waitingOn,
            choices,
            editForm,
            editOpen,
            teamChoices,
            canManage ? await EventsAsync(task.EventId, ct) : [],
            statusForm ?? new StatusForm { Status = task.Status, BlockedNote = task.BlockedNote },
            access.PlaceOn(task),
            canManage ? RequestsOn([task]) : []);
    }

    /// <summary>Waiting requests, oldest first.</summary>
    private static IReadOnlyList<RequestRow> RequestsOn(IEnumerable<TaskInfo> onTasks) =>
        [.. onTasks
            .SelectMany(t => t.WaitingRequests.Select(a => (Task: t, Assignment: a)))
            .OrderBy(r => r.Assignment.CreatedAt)
            .Select(r => new RequestRow(r.Task, r.Assignment.UserId))];

    /// <summary>Teams that can hold tasks and that the person manages, plus the task's own team so the form never drops it.</summary>
    private async Task<IReadOnlyList<TeamChoice>> ManagedTeamsAsync(TasksAccess access, Guid? currentTeamId, CancellationToken ct) =>
        [.. (await teams.GetTeamsAsync(ct)).Values
            .Where(t => t.Id == currentTeamId || (t.IsActive && !t.IsSystemTeam && access.Manages(t.Id)))
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(t => new TeamChoice(t.Id, t.Name, t.Slug))];

    /// <summary>Events a task can be tied to: every one not withdrawn, plus the one the task already has.</summary>
    private async Task<IReadOnlyList<EventOption>> EventsAsync(Guid? currentEventId, CancellationToken ct) =>
        [.. (await settings.GetAllEventSettingsAsync(ct))
            .Where(e => e.Status != EventSettingsStatus.Deleted || e.Id == currentEventId)
            .OrderByDescending(e => e.GateOpeningDate)
            .Select(e => new EventOption(e.Id, e.EventName))];

    /// <summary>The slug, the custom slug or the team id.</summary>
    private async Task<TeamInfo?> FindTeamAsync(string slug, CancellationToken ct) =>
        await teams.GetTeamBySlugAsync(slug, ct)
        ?? (Guid.TryParse(slug, out var id) ? await teams.GetTeamAsync(id, ct) : null);
}
