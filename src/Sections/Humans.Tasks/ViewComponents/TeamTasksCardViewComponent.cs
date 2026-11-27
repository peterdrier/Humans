using Humans.Base.Extensions;
using Humans.Permissions.Contracts;
using Humans.Tasks.Authorization;
using Humans.Tasks.Models;
using Humans.Tasks.Services;
using Humans.Teams.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.Tasks.ViewComponents;

/// <summary>
/// A team's open tasks on the team page. Parameters bind from <see cref="TeamTasksCardArgs"/>. Shows what the viewer may
/// see, a few rows of it, and for a manager the requests waiting. A team that cannot hold tasks and has none shows nothing.
/// </summary>
internal sealed class TeamTasksCardViewComponent(
    ITaskService tasks,
    ITeamService teams,
    IPermissionService permissions,
    IAuthorizationService authorization,
    IStringLocalizer<TasksResource> localizer) : ViewComponent
{
    /// <summary>The card is a glance; the team's tasks page has the rest.</summary>
    internal const int RowLimit = 5;

    public async Task<IViewComponentResult> InvokeAsync(Guid teamId)
    {
        var ct = HttpContext.RequestAborted;
        if (await TasksAccess.ForAsync(UserClaimsPrincipal, permissions, authorization, ct) is not { } access) return Content(string.Empty);
        if (await teams.GetTeamAsync(teamId, ct) is not { } team) return Content(string.Empty);

        var all = await tasks.GetTasksAsync(ct);
        var onTeam = all.Where(t => t.Owner.Id == teamId).ToList();
        if (!access.CanSeeTeam(team.Id, team.IsHidden, onTeam)) return Content(string.Empty);

        var open = TaskRowOrder.Order((await tasks.GetListedTasksAsync(new TaskListFilter().ToScope(), ct))
                .Where(t => t.Owner.Id == teamId && access.CanSee(t)))
            .ToList();
        var canAdd = access.Manages(team.Id) && team.IsActive && !team.IsSystemTeam;
        if (open.Count == 0 && (!team.IsActive || team.IsSystemTeam)) return Content(string.Empty);

        var shown = open.Take(RowLimit).ToList();
        var back = HttpContext.Request.Path + HttpContext.Request.QueryString.Value;
        var tasksUrl = Url.Action("Team", "Tasks", new { slug = team.Slug.Length > 0 ? team.Slug : team.Id.ToString() })!;
        var waiting = access.Manages(team.Id) ? onTeam.Sum(t => t.WaitingRequests.Count()) : 0;

        return View(new TeamTasksCardViewModel(
            new TaskRowsModel(shown, ShowTeam: false, ShowStatus: false, access.PlacesOn(shown), access.BlockersOn(shown, all), back,
                localizer["Tasks_Card_Empty"].Value, Flush: true),
            open.Count, waiting, canAdd, tasksUrl, tasksUrl + "#requests", tasksUrl + "#add"));
    }
}
