using Humans.Application.Interfaces;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Dashboard;
using Humans.Application.Interfaces.Feedback;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.Controllers;

[Route("Admin")]
public class AdminController(IUserServiceRead userService) : HumansControllerBase(userService)
{
    // AnyAdminRole so top-nav doesn't 403 for FinanceAdmin etc.; tiles are aggregate counts safe across roles.
    [HttpGet("")]
    [Authorize(Policy = PolicyNames.AnyAdminRole)]
    public async Task<IActionResult> Index(
        [FromServices] IShiftManagementService shifts,
        [FromServices] IFeedbackService feedback,
        [FromServices] IAuditViewerService auditViewer,
        [FromServices] IAdminDashboardService adminDashboardService,
        [FromServices] IUserServiceRead userService,
        [FromServices] IUserActivityTracker activityTracker,
        CancellationToken ct)
    {
        var firstName = User.Identity?.Name?.Split(' ').FirstOrDefault() ?? "";
        var snapshot = await userService.GetAllUserInfosAsync(ct);
        var totalUsers = snapshot.Count;
        var activeProfileUsers = snapshot.Count(u => u.IsActive);
        var activeEvent = await shifts.GetActiveAsync();
        var ticketHolders = activeEvent is { Year: > 0 }
            ? snapshot.Count(u => u.HasTicketForYear(activeEvent.Year))
            : 0;
        var (filled, total, ratio) = await shifts.GetOverallCoverageAsync(ct);
        var openFeedback = await feedback.GetActionableCountAsync(ct);
        var recent = (await auditViewer.GetRecentAsync(8, ct))
            .Select(e => new DashboardActivityRow(e.Action, e.Description, e.OccurredAt))
            .ToArray();
        var staffing = Array.Empty<DepartmentCoverage>();

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

        var vm = new AdminDashboardViewModel(
            GreetingFirstName: firstName,
            TotalUsers: totalUsers,
            ActiveProfileUsers: activeProfileUsers,
            TicketHolders: ticketHolders,
            ShiftCoveragePercent: total > 0 ? (int)Math.Round(ratio * 100) : 0,
            ShiftFilledOf: total > 0 ? filled : null,
            ShiftTotalOf: total > 0 ? total : null,
            OpenFeedback: openFeedback,
            OnlineNow: activityTracker.CountActiveWithin(Duration.FromMinutes(5)),
            OnlineLastHour: activityTracker.CountActiveWithin(Duration.FromHours(1)),
            OnlineLast24h: activityTracker.CountActiveWithin(Duration.FromHours(24)),
            StaffingByDepartment: staffing,
            RecentActivity: recent,
            AppStats: appStats,
            LanguageDistribution: languages,
            SetMembership: dashboardData.SetMembership);
        return View(vm);
    }
}
