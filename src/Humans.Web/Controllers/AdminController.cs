using Humans.Application.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Application.Interfaces.Dashboard;
using Humans.Email.Contracts;
using Humans.Expenses.Contracts;
using Humans.Feedback.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Store.Contracts;
using Humans.UI.Authorization;
using Humans.UI.Controllers;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Web.Controllers;

[Route("Admin")]
public class AdminController(IUserServiceRead userService) : HumansControllerBase(userService)
{
    // AnyAdminRole so top-nav doesn't 403 for FinanceAdmin etc.; tiles are aggregate counts safe across roles.
    [HttpGet("")]
    [Authorize(Policy = PolicyNames.AnyAdminRole)]
    public async Task<IActionResult> Index(
        [FromServices] IBurnSettingsService burnSettings,
        [FromServices] IShiftManagementServiceRead shifts,
        [FromServices] IFeedbackServiceRead feedback,
        [FromServices] IAuditViewerService auditViewer,
        [FromServices] IAdminDashboardService adminDashboardService,
        [FromServices] IUserServiceRead userService,
        [FromServices] IUserActivityTracker activityTracker,
        [FromServices] ITeamServiceRead teams,
        [FromServices] IEmailOutboxServiceRead emailOutbox,
        [FromServices] IStoreServiceRead storeService,
        [FromServices] IExpenseReportServiceRead expenseReportService,
        [FromServices] IAuthorizationService authorizationService,
        CancellationToken ct)
    {
        var firstName = User.Identity?.Name?.Split(' ').FirstOrDefault() ?? "";
        var snapshot = await userService.GetAllUserInfosAsync(ct);
        var totalUsers = snapshot.Count;
        var activeProfileUsers = snapshot.Count(u => u.IsActive);
        var activeEvent = await burnSettings.GetActiveAsync(ct);
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

        var teamCount = (await teams.GetTeamsAsync(ct)).Count;
        // Reuse of the existing page read: page size 1, no filter — TotalCount is the tile.
        var auditTotal = (await auditViewer.GetPageAsync(null, 1, 1, ct)).TotalCount;
        var emailStats = await emailOutbox.GetOutboxStatsAsync(0, ct);

        // The Store/Expenses tiles are policy-gated below AnyAdminRole (mirroring their source
        // pages' own tighter policies), and the computation is skipped along with the tile for
        // the roles that can't see it — no point pulling the whole store summary or every
        // expense report for a viewer who will never see the number.
        var canSeeStoreTile = (await authorizationService.AuthorizeAsync(User, PolicyNames.StoreCatalogAdmin)).Succeeded;
        int? storeOrders = null;
        decimal? storeTotalEur = null;
        if (canSeeStoreTile && activeEvent is { Year: > 0 })
        {
            var storeSummary = await storeService.GetStoreSummaryAsync(activeEvent.Year, ct);
            storeOrders = storeSummary.ByCounterparty.Count;
            storeTotalEur = storeSummary.ByCounterparty.Sum(o => o.TotalDueEur);
        }

        var canSeeExpenseTile = (await authorizationService.AuthorizeAsync(User, PolicyNames.FinanceAdminOrAdmin)).Succeeded;
        var expenseReportCount = 0;
        var expenseTotalEur = 0m;
        if (canSeeExpenseTile)
        {
            var expenseReports = await expenseReportService.GetAllAsync(ct);
            expenseReportCount = expenseReports.Count;
            expenseTotalEur = expenseReports.Sum(r => r.Total);
        }

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
            SetMembership: dashboardData.SetMembership,
            TotalTeams: teamCount,
            TotalAuditEvents: auditTotal,
            TotalEmails: emailStats.TotalCount,
            StoreOrders: storeOrders,
            StoreTotalEur: storeTotalEur,
            ExpenseReports: expenseReportCount,
            ExpenseTotalEur: expenseTotalEur);
        return View(vm);
    }
}
