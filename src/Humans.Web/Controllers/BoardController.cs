using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Extensions;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize(Roles = "Board,Admin")]
[Route("Board")]
public class BoardController : Controller
{
    private readonly IAuditLogService _auditLogService;
    private readonly IOnboardingService _onboardingService;
    private readonly ITeamResourceService _teamResourceService;
    private readonly UserManager<User> _userManager;
    private readonly ILogger<BoardController> _logger;

    public BoardController(
        IAuditLogService auditLogService,
        IOnboardingService onboardingService,
        ITeamResourceService teamResourceService,
        UserManager<User> userManager,
        ILogger<BoardController> logger)
    {
        _auditLogService = auditLogService;
        _onboardingService = onboardingService;
        _teamResourceService = teamResourceService;
        _userManager = userManager;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var dashboardData = await _onboardingService.GetAdminDashboardAsync();
        var recentEntries = await _auditLogService.GetRecentAsync(15);

        var viewModel = new AdminDashboardViewModel
        {
            TotalMembers = dashboardData.TotalMembers,
            ActiveMembers = dashboardData.ActiveMembers,
            PendingVolunteers = dashboardData.PendingApproval,
            PendingApplications = dashboardData.PendingApplications,
            PendingConsents = dashboardData.MissingConsents,
            RecentActivity = recentEntries.Select(e => new RecentActivityViewModel
            {
                Description = e.Description,
                Timestamp = e.OccurredAt.ToDateTimeUtc(),
                Type = e.Action.ToString()
            }).ToList(),
            TotalApplications = dashboardData.TotalApplications,
            ApprovedApplications = dashboardData.ApprovedApplications,
            RejectedApplications = dashboardData.RejectedApplications,
            ColaboradorApplied = dashboardData.ColaboradorApplied,
            AsociadoApplied = dashboardData.AsociadoApplied
        };

        return View(viewModel);
    }

    [HttpGet("AuditLog")]
    public async Task<IActionResult> AuditLog(string? filter, int page = 1)
    {
        var pageSize = 50;
        var (items, totalCount, anomalyCount) = await _auditLogService.GetFilteredAsync(filter, page, pageSize);

        var entries = items.Select(e => new AuditLogEntryViewModel
        {
            Action = e.Action.ToString(),
            Description = e.Description,
            OccurredAt = e.OccurredAt.ToDateTimeUtc(),
            ActorName = e.ActorName,
            IsSystemAction = e.ActorUserId == null
        }).ToList();

        var viewModel = new AuditLogListViewModel
        {
            Entries = entries,
            ActionFilter = filter,
            AnomalyCount = anomalyCount,
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize
        };

        return View(viewModel);
    }

    [HttpPost("AuditLog/CheckDriveActivity")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckDriveActivity(
        [FromServices] IDriveActivityMonitorService monitorService)
    {
        var currentUser = await _userManager.GetUserAsync(User);

        try
        {
            var count = await monitorService.CheckForAnomalousActivityAsync();
            _logger.LogInformation("Board {UserId} triggered manual Drive activity check: {Count} anomalies",
                currentUser?.Id, count);

            TempData["SuccessMessage"] = count > 0
                ? $"Drive activity check completed: {count} anomalous change(s) detected."
                : "Drive activity check completed: no anomalies detected.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual Drive activity check failed");
            TempData["ErrorMessage"] = "Drive activity check failed. Check logs for details.";
        }

        return RedirectToAction(nameof(AuditLog), new { filter = nameof(AuditAction.AnomalousPermissionDetected) });
    }

    [HttpGet("GoogleSync/Resource/{id:guid}/Audit")]
    public async Task<IActionResult> GoogleSyncResourceAudit(Guid id)
    {
        var resource = await _teamResourceService.GetResourceByIdAsync(id);

        if (resource == null)
        {
            return NotFound();
        }

        var entries = await _auditLogService.GetByResourceAsync(id);

        var viewModel = new GoogleSyncAuditListViewModel
        {
            Title = $"Sync Audit: {resource.Name}",
            BackUrl = Url.Action("Sync", "Team"),
            BackLabel = "Back to Sync Status",
            Entries = entries.Select(e => new GoogleSyncAuditEntryViewModel
            {
                Action = e.Action.ToString(),
                Description = e.Description,
                UserEmail = e.UserEmail,
                Role = e.Role,
                SyncSource = e.SyncSource?.ToString(),
                OccurredAt = e.OccurredAt.ToDateTimeUtc(),
                Success = e.Success,
                ErrorMessage = e.ErrorMessage,
                ActorName = e.ActorName,
                RelatedEntityId = e.RelatedEntityId
            }).ToList()
        };

        return View("GoogleSyncAudit", viewModel);
    }

}
