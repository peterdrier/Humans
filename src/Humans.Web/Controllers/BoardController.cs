using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Humans.Application.Interfaces.AuditLog;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.BoardOrAdmin)]
[Route("Board")]
public class BoardController : HumansControllerBase
{
    private readonly IAuditViewerService _auditViewer;

    public BoardController(
        IAuditViewerService auditViewer,
        UserManager<User> userManager)
        : base(userManager)
    {
        _auditViewer = auditViewer;
    }

    [HttpGet("AuditLog")]
    public async Task<IActionResult> AuditLog(string? filter, int page = 1)
    {
        var pageSize = 50;
        var result = await _auditViewer.GetPageAsync(filter, page, pageSize);

        var viewModel = new AuditLogListViewModel
        {
            Events = result.Items,
            ActionFilter = filter,
            AnomalyCount = result.AnomalyCount,
            TotalCount = result.TotalCount,
            PageNumber = page,
            PageSize = pageSize
        };

        return View("~/Views/Shared/AuditLog.cshtml", viewModel);
    }
}
