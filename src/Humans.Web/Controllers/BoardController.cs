using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Dashboard;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.BoardOrAdmin)]
[Route("Board")]
public class BoardController : HumansControllerBase
{
    private readonly IAuditViewerService _auditViewer;
    private readonly IAdminDashboardService _adminDashboardService;

    public BoardController(
        IAuditViewerService auditViewer,
        IAdminDashboardService adminDashboardService,
        UserManager<User> userManager)
        : base(userManager)
    {
        _auditViewer = auditViewer;
        _adminDashboardService = adminDashboardService;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var dashboardData = await _adminDashboardService.GetAdminDashboardAsync();
        var recentEvents = await _auditViewer.GetRecentAsync(15);

        var viewModel = new BoardDashboardViewModel
        {
            TotalMembers = dashboardData.TotalMembers,
            IncompleteSignup = dashboardData.IncompleteSignup,
            PendingApproval = dashboardData.PendingApproval,
            ActiveMembers = dashboardData.ActiveMembers,
            MissingConsents = dashboardData.MissingConsents,
            Suspended = dashboardData.Suspended,
            PendingDeletion = dashboardData.PendingDeletion,
            PendingApplications = dashboardData.PendingApplications,
            RecentActivity = recentEvents.Select(e => new RecentActivityViewModel
            {
                Description = e.Description,
                Timestamp = e.OccurredAt.ToDateTimeUtc(),
                Type = e.Action
            }).ToList(),
            TotalApplications = dashboardData.TotalApplications,
            ApprovedApplications = dashboardData.ApprovedApplications,
            RejectedApplications = dashboardData.RejectedApplications,
            ColaboradorApplied = dashboardData.ColaboradorApplied,
            AsociadoApplied = dashboardData.AsociadoApplied,
            LanguageDistribution = dashboardData.LanguageDistribution
                .Select(l => new LanguageCountViewModel { Language = l.Language, Count = l.Count })
                .ToList()
        };

        return View(viewModel);
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
