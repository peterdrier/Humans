using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

using Humans.Web.Services.Dashboard;

namespace Humans.Web.Controllers;

[Route("Admin")]
public class AdminController(IUserServiceRead userService) : HumansControllerBase(userService)
{
    // AnyAdminRole so top-nav doesn't 403 for FinanceAdmin etc.; the summary strip is
    // aggregate counts safe across roles, and the tiles that aren't carry their own policy.
    [HttpGet("")]
    [Authorize(Policy = PolicyNames.AnyAdminRole)]
    public async Task<IActionResult> Index(
        [FromServices] IAdminDashboardService adminDashboardService,
        CancellationToken ct)
    {
        var dashboardData = await adminDashboardService.GetAdminDashboardAsync(ct);
        var appStats = new DashboardApplicationStats(
            Total: dashboardData.TotalApplications,
            Approved: dashboardData.ApprovedApplications,
            Rejected: dashboardData.RejectedApplications,
            Colaborador: dashboardData.ColaboradorApplied,
            Asociado: dashboardData.AsociadoApplied);
        var languages = dashboardData.LanguageDistribution
            .Select(l => new DashboardLanguageCount(l.Language, l.Count))
            .ToArray();

        return View(new AdminDashboardViewModel(
            AppStats: appStats,
            LanguageDistribution: languages,
            SetMembership: dashboardData.SetMembership));
    }
}
