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

namespace Humans.Tasks.Controllers;

[Authorize]
[Route("Tasks")]
internal sealed class TasksController(
    ITaskService tasks,
    IPermissionService permissions,
    ITeamService teams,
    ISettingsService settings,
    IUserService userService,
    IStringLocalizer<TasksResource> localizer) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] TaskListFilter filter, CancellationToken ct)
    {
        if (await TasksAccess.ForAsync(User, permissions, ct) is not { } access) return Unauthorized();

        var visible = (await ListAsync(filter, ct)).Where(access.CanSee).ToList();
        return View(new TaskListViewModel(visible, await EventsAsync(currentEventId: null, ct), filter));
    }

    [HttpGet("Team/{slug}")]
    public async Task<IActionResult> Team(string slug, [FromQuery] TaskListFilter filter, CancellationToken ct)
    {
        if (await TasksAccess.ForAsync(User, permissions, ct) is not { } access) return Unauthorized();
        if (await FindTeamAsync(slug, ct) is not { } team) return NotFound();

        var onTeam = (await tasks.GetTasksAsync(ct)).Where(t => t.Owner.Id == team.Id).ToList();
        if (!access.CanSeeTeam(team.Id, team.IsHidden, onTeam)) return NotFound();

        return View(await TeamPageAsync(team, access, onTeam, filter, new TaskForm { TeamId = team.Id }, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        if (await TasksAccess.ForAsync(User, permissions, ct) is not { } access) return Unauthorized();
        if (await tasks.GetTaskAsync(id, ct) is not { } task || !access.CanSee(task)) return NotFound();

        return View(await TaskPageAsync(task, access, TaskForm.From(task), editOpen: false, ct));
    }

    [HttpPost("Team/{slug}/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(string slug, TaskForm form, CancellationToken ct)
    {
        if (await TasksAccess.ForAsync(User, permissions, ct) is not { } access) return Unauthorized();
        if (await FindTeamAsync(slug, ct) is not { } team) return NotFound();
        if (!access.Manages(team.Id) || form.TeamId != team.Id) return Forbid();

        var result = await tasks.CreateAsync(form.ToInput(), access.UserId, ct);
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
        if (await TasksAccess.ForAsync(User, permissions, ct) is not { } access) return Unauthorized();
        if (await tasks.GetTaskAsync(id, ct) is not { } task || !access.CanSee(task)) return NotFound();
        if (!access.Manages(task) || (form.TeamId != task.Owner.Id && !access.CanMove(task, form.TeamId))) return Forbid();

        var result = await tasks.UpdateAsync(id, form.ToInput(), access.UserId, ct);
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
    public async Task<IActionResult> SetStatus(Guid id, StatusForm form, CancellationToken ct) =>
        await ChangeAsync(id, ct, (task, access) => access.CanSetStatus(task, form.Status),
            (task, access) => tasks.SetStatusAsync(id, form.Status, form.BlockedNote, access.UserId, ct), "Tasks_StatusChanged");

    [HttpPost("{id:guid}/Assign")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Assign(Guid id, Guid userId, CancellationToken ct) =>
        await ChangeAsync(id, ct, (task, access) => access.Manages(task), async (task, access) =>
            await FindUserInfoByIdAsync(userId, ct) is null
                ? new TaskResult(Refusal: localizer["Tasks_Refusal_NoPerson"].Value)
                : await tasks.AssignAsync(id, userId, access.UserId, ct), "Tasks_Assigned");

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

    [HttpPost("{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (await TasksAccess.ForAsync(User, permissions, ct) is not { } access) return Unauthorized();
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
    private async Task<IActionResult> ChangeAsync(
        Guid id, CancellationToken ct,
        Func<TaskInfo, TasksAccess, bool> allowed,
        Func<TaskInfo, TasksAccess, Task<TaskResult>> change,
        string successKey)
    {
        if (await TasksAccess.ForAsync(User, permissions, ct) is not { } access) return Unauthorized();
        if (await tasks.GetTaskAsync(id, ct) is not { } task || !access.CanSee(task)) return NotFound();
        if (!allowed(task, access)) return Forbid();

        var result = await change(task, access);
        if (result.Succeeded) SetSuccess(localizer[successKey].Value);
        else SetError(result.Refusal!);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>The default list comes from the service; any other filter narrows or widens every task.</summary>
    private async Task<IReadOnlyList<TaskInfo>> ListAsync(TaskListFilter filter, CancellationToken ct)
    {
        if (filter.IsDefault) return await tasks.GetListedTasksAsync(ct);

        var activeEventId = settings.GetActiveEventSettings()?.Id;
        return (await tasks.GetTasksAsync(ct))
            .Where(t => (filter.Finished || !t.IsFinished) && filter.EventChoice switch
            {
                TaskListFilter.ActiveEvent => t.EventId is null || t.EventId == activeEventId,
                TaskListFilter.AnyEvent => true,
                _ => t.EventId == filter.SpecificEventId,
            })
            .ToList();
    }

    private async Task<TeamPageViewModel> TeamPageAsync(
        TeamInfo team, TasksAccess access, IReadOnlyList<TaskInfo> onTeam, TaskListFilter filter, TaskForm addForm, CancellationToken ct)
    {
        var listed = (await ListAsync(filter, ct)).Where(t => t.Owner.Id == team.Id && access.CanSee(t)).ToList();
        var canAdd = access.Manages(team.Id) && team.IsActive && !team.IsSystemTeam;
        return new TeamPageViewModel(
            team.Id, team.Name, team.Slug, listed, filter, canAdd, addForm, await EventsAsync(addForm.EventId, ct));
    }

    private async Task<TaskPageViewModel> TaskPageAsync(
        TaskInfo task, TasksAccess access, TaskForm editForm, bool editOpen, CancellationToken ct)
    {
        var all = await tasks.GetTasksAsync(ct);
        var visibleById = all.Where(access.CanSee).ToDictionary(t => t.Id);
        var canManage = access.Manages(task);
        var waitingOn = task.WaitingOn
            .Select(l => new DependencyRow(l.Id, visibleById.ContainsKey(l.Id) ? l.Title : null, l.Status))
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
            canManage ? await EventsAsync(task.EventId, ct) : []);
    }

    /// <summary>Teams that can hold tasks and that the person manages, plus the task's own team so the form never drops it.</summary>
    private async Task<IReadOnlyList<TeamChoice>> ManagedTeamsAsync(TasksAccess access, Guid currentTeamId, CancellationToken ct) =>
        [.. (await teams.GetTeamsAsync(ct)).Values
            .Where(t => t.Id == currentTeamId || (t.IsActive && !t.IsSystemTeam && access.Manages(t.Id)))
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(t => new TeamChoice(t.Id, t.Name))];

    /// <summary>Events a task can be tied to: every one not withdrawn, plus the one the task already has.</summary>
    private async Task<IReadOnlyList<EventOption>> EventsAsync(Guid? currentEventId, CancellationToken ct) =>
        [.. (await settings.GetAllEventSettingsAsync(ct))
            .Where(e => e.Status != EventSettingsStatus.Deleted || e.Id == currentEventId)
            .OrderByDescending(e => e.GateOpeningDate)
            .Select(e => new EventOption(e.Id, e.EventName))];

    /// <summary>The slug, the custom slug or the team id.</summary>
    private async Task<TeamInfo?> FindTeamAsync(string slug, CancellationToken ct)
    {
        var all = await teams.GetTeamsAsync(ct);
        return all.Values.FirstOrDefault(t =>
                string.Equals(t.Slug, slug, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.CustomSlug, slug, StringComparison.OrdinalIgnoreCase))
            ?? (Guid.TryParse(slug, out var id) ? all.GetValueOrDefault(id) : null);
    }
}
