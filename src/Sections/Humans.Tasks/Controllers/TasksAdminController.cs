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
    public async Task<IActionResult> Index(CancellationToken ct) => View(await tasks.GetAdminSummaryAsync(ct));
}
