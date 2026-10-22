using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.AuditLog.Contracts;
using Humans.AuditLog.Models;
using Humans.Base.Authorization;
using Humans.Users.Contracts;

namespace Humans.AuditLog.Controllers;

[Route("AuditLog")]
internal sealed class AuditLogController(
    IUserService userService,
    IAuditViewerService auditViewer) : HumansControllerBase(userService)
{
    [HttpGet("")]
    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
    public async Task<IActionResult> Index(string? filter, int page = 1)
    {
        var pageSize = 50;
        var result = await auditViewer.GetPageAsync(filter, page, pageSize, HttpContext.RequestAborted);

        var viewModel = new AuditLogListViewModel
        {
            Events = result.Items,
            ActionFilter = filter,
            AnomalyCount = result.AnomalyCount,
            TotalCount = result.TotalCount,
            PageNumber = page,
            PageSize = pageSize
        };

        return View(viewModel);
    }
}
