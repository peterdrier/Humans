using Humans.Application.Interfaces.Shifts.Workload;
using Humans.Application.Interfaces.Users;
using Humans.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>Site-wide workload dashboard (read-only). see #734.</summary>
[Authorize(Policy = PolicyNames.ShiftDashboardAccess)]
[Route("Shifts/Admin/Workload")]
public class ShiftWorkloadAdminController(IUserServiceRead userService, IWorkloadService workloadService)
    : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var report = await workloadService.GetForActiveEventAsync(ct);
        if (report is null)
            return View(report);

        return View(report with
        {
            ByPerson = report.ByPerson
                .OrderByDescending(r => r.TotalHours)
                .ThenBy(r => r.DisplayName, StringComparer.Ordinal)
                .ToList(),
            ByRota = report.ByRota
                .OrderBy(r => r.TeamName, StringComparer.Ordinal)
                .ThenBy(r => r.RotaName, StringComparer.Ordinal)
                .ToList(),
            ByDepartment = report.ByDepartment
                .OrderBy(r => r.TeamName, StringComparer.Ordinal)
                .ToList(),
        });
    }
}
