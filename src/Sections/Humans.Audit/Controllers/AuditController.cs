using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Audit.Services;
using Humans.Audit.Models;
using Humans.Base.Authorization;
using Humans.Users.Contracts;

namespace Humans.Audit.Controllers;

[Route("Audit")]
internal sealed class AuditController(
    IUserService userService,
    AuditViewerService auditViewer,
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
            Entries = result.Items,
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
