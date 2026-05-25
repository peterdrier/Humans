using Humans.Application.DTOs.Shifts.Workload;
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
        return View(SortForDisplay(report));
    }

    // Display sort at presentation layer (memory/architecture/display-sort-in-controllers.md).
    private static WorkloadReport? SortForDisplay(WorkloadReport? report)
    {
        if (report is null) return null;

        var byRota = report.ByRota
            .OrderBy(r => r.TeamName, StringComparer.Ordinal)
            .ThenBy(r => r.RotaName, StringComparer.Ordinal)
            .ToList();

        var byDepartment = report.ByDepartment
            .OrderBy(r => r.TeamName, StringComparer.Ordinal)
            .ToList();

        var byPerson = report.ByPerson
            .OrderByDescending(r => r.TotalHours)
            .ThenBy(r => r.DisplayName, StringComparer.Ordinal)
            .ToList();

        return report with
        {
            ByPerson = byPerson,
            ByRota = byRota,
            ByDepartment = byDepartment,
        };
    }
}
