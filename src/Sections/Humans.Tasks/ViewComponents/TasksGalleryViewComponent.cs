using Humans.Base.Authorization;
using Humans.Tasks.Domain;
using Humans.Tasks.Models;
using Humans.Tasks.Services;
using Humans.Teams.Contracts;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Tasks.ViewComponents;

/// <summary>The widget gallery's card for the task rows, drawn from made-up tasks so it needs no data and no signed-in user.</summary>
internal sealed class TasksGalleryViewComponent : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        var team = Guid.NewGuid();
        var fence = Sample(team, "Build the perimeter fence", SlotPriority.Critical, slots: 4, confirmed: 1, due: new LocalDate(2026, 7, 1));
        var signs = Sample(team, "Paint the signs", SlotPriority.Important, slots: 2, confirmed: 2, waitingOn: fence);
        var map = Sample(team, "Draw the site map", SlotPriority.None, slots: 1, confirmed: 0, needsApproval: true);
        var tasks = new[] { fence, signs, map };
        return View(new TaskRowsModel(
            tasks, ShowTeam: true, ShowStatus: true, new Dictionary<Guid, MyPlace>(),
            new Dictionary<Guid, string?> { [signs.Id] = fence.Title }, "/Debug/WidgetGallery", string.Empty));
    }

    private static TaskInfo Sample(
        Guid team, string title, SlotPriority priority, int slots, int confirmed,
        LocalDate? due = null, TaskInfo? waitingOn = null, bool needsApproval = false) =>
        new(Guid.NewGuid(), TeamOwner.Of(team), "Production", "production", false, title, null, null, null, null,
            due is { } d ? new TaskDateInfo(d, null, d) : null,
            TaskItemStatus.Todo, null, priority, slots, needsApproval, needsApproval ? "Needs a steady hand" : null, false,
            [.. Enumerable.Range(0, confirmed).Select(_ => new TaskAssignmentInfo(Guid.NewGuid(), true, Instant.FromUtc(2026, 1, 1, 0, 0)))],
            waitingOn is null ? [] : [new TaskLinkInfo(waitingOn.Id, waitingOn.Title, waitingOn.Status)]);
}
