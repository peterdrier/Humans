using Humans.Application.Interfaces.Shifts.Workload;
using Humans.Application.Interfaces.Users;
using Humans.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>
/// Site-wide workload aggregation dashboard. Reads only — no mutations.
/// Gated to the narrow <see cref="PolicyNames.ShiftDashboardAccess"/> policy
/// (Admin / NoInfoAdmin / VolunteerCoordinator); the regular department
/// coordinator surface stays on <c>/Shifts/Dashboard</c>.
/// </summary>
/// <remarks>
/// nobodies-collective/Humans#734. Role-based hours (role hours + shift hours
/// merged) are deferred — see issue follow-up.
/// </remarks>
[Authorize(Policy = PolicyNames.ShiftDashboardAccess)]
[Route("Admin/Workload")]
public class AdminWorkloadController : HumansControllerBase
{
    private readonly IWorkloadService _workloadService;

    public AdminWorkloadController(IUserService userService, IWorkloadService workloadService)
        : base(userService)
    {
        _workloadService = workloadService;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var report = await _workloadService.GetForActiveEventAsync(ct);
        return View(report);
    }
}
