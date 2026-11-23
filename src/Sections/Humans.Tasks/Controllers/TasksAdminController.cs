using Humans.Base.Controllers;
using Humans.Tasks.Contracts;
using Humans.Tasks.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Tasks.Controllers;

[Authorize(Policy = TasksPolicies.Read)]
[Route("Tasks/Admin")]
internal sealed class TasksAdminController(ITaskService tasks, IUserService userService) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var summary = await tasks.GetAdminSummaryAsync(ct);
        return View(summary with
        {
            Teams = [.. summary.Teams.OrderBy(r => r.TeamName, StringComparer.CurrentCultureIgnoreCase)],
            BlockedTasks =
            [
                .. summary.BlockedTasks
                    .OrderBy(t => t.TeamName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
            ],
        });
    }
}
