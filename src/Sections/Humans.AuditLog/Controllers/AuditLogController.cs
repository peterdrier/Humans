using Humans.UI.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.AuditLog.Contracts;
using Humans.AuditLog.Models;
using Humans.Application.Interfaces.Users;
using Humans.UI.Authorization;
using Humans.Users.Contracts;

namespace Humans.AuditLog.Controllers;

/// <summary>
/// The general audit browser, over the section's own <see cref="IAuditViewerService"/> —
/// which resolves actor, subject and team display names, and moved here from
/// <c>Humans.Application</c> in G5 lane 4b-2h (nobodies-collective/Humans#866).
/// </summary>
/// <remarks>
/// The Google-sync audit views and the Drive-activity scan that used to live here are in
/// <c>Humans.Monitor</c>, carved out when AuditLog was still forbidden from naming a vertical
/// section. That constraint was lifted by Peter's 2026-08-14 Base-floor decision, and
/// <c>AuditLogArchitectureTests.SectionReferencesNoVerticalSection</c> was retired with it;
/// the Monitor split stands on its own merits (a distinct read model and a job) rather than on
/// that rule.
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
