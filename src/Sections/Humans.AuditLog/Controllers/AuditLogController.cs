using Humans.UI.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces.AuditLog;
using Humans.AuditLog.Models;
using Humans.Application.Interfaces.Users;
using Humans.UI.Authorization;
using Humans.Users.Contracts;

namespace Humans.AuditLog.Controllers;

/// <summary>
/// The general audit browser. Reaches no section: <see cref="IAuditViewerService"/> is the
/// Base orchestrator that resolves actor, subject and team display names, and it stayed in
/// <c>Humans.Application</c> at AuditLog's own G5 for exactly that reason.
/// </summary>
/// <remarks>
/// The Google-sync audit views and the Drive-activity scan that used to live here moved to
/// <c>Humans.Monitor</c>: two of them injected GoogleIntegration services, and AuditLog is a
/// *horizontal* — <c>peters-hard-rules.md</c> forbids a horizontal from referencing a vertical
/// section. <c>AuditLogArchitectureTests.SectionReferencesNoVerticalSection</c> is what holds
/// that line.
/// </remarks>
[Route("AuditLog")]
internal sealed class AuditLogController(
    IUserServiceRead userService,
    IAuditViewerService auditViewer) : HumansControllerBase(userService)
{
    [HttpGet("")]
    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
    public async Task<IActionResult> Index(string? filter, int page = 1)
    {
        var pageSize = 50;
        var result = await auditViewer.GetPageAsync(filter, page, pageSize);

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
