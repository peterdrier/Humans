using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.AuditLog.Contracts;
using Humans.AuditLog.Models;
using Humans.AuditLog.Services;
using Humans.Base.Authorization;
using Humans.Users.Contracts;

namespace Humans.AuditLog.Controllers;

[Route("AuditLog")]
internal sealed class AuditLogController(
    IUserService userService,
    IAuditViewerService auditViewer,
    AuditActionRegistry actions) : HumansControllerBase(userService)
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
            QuickFilters = actions.QuickFilters,
            Alerts = actions.Alerts,
            AlertCounts = result.AlertCounts,
            TotalCount = result.TotalCount,
            PageNumber = page,
            PageSize = pageSize
        };

        return View(viewModel);
    }
}
