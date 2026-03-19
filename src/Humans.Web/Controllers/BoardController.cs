using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Web.Extensions;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize(Roles = RoleGroups.BoardOrAdmin)]
[Route("Board")]
public class BoardController : HumansControllerBase
{
    private readonly IAuditLogService _auditLogService;
    private readonly IOnboardingService _onboardingService;
    private readonly ITeamResourceService _teamResourceService;
    private readonly HumansDbContext _dbContext;
    private readonly ILogger<BoardController> _logger;

    public BoardController(
        IAuditLogService auditLogService,
        IOnboardingService onboardingService,
        ITeamResourceService teamResourceService,
        UserManager<User> userManager,
        HumansDbContext dbContext,
        ILogger<BoardController> logger)
        : base(userManager)
    {
        _auditLogService = auditLogService;
        _onboardingService = onboardingService;
        _teamResourceService = teamResourceService;
        _dbContext = dbContext;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var dashboardData = await _onboardingService.GetAdminDashboardAsync();
        var recentEntries = await _auditLogService.GetRecentAsync(15);

        var languageDistribution = await _dbContext.Users
            .Where(u => u.Profile != null && u.Profile.IsApproved && !u.Profile.IsSuspended)
            .GroupBy(u => u.PreferredLanguage)
            .Select(g => new { Language = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ToListAsync();

        var viewModel = new AdminDashboardViewModel
        {
            TotalMembers = dashboardData.TotalMembers,
            IncompleteSignup = dashboardData.IncompleteSignup,
            PendingApproval = dashboardData.PendingApproval,
            ActiveMembers = dashboardData.ActiveMembers,
            MissingConsents = dashboardData.MissingConsents,
            Suspended = dashboardData.Suspended,
            PendingDeletion = dashboardData.PendingDeletion,
            PendingApplications = dashboardData.PendingApplications,
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
            AsociadoApplied = dashboardData.AsociadoApplied,
            LanguageDistribution = languageDistribution
                .Select(l => new LanguageCountViewModel { Language = l.Language, Count = l.Count })
                .ToList()
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

        return View("~/Views/Shared/AuditLog.cshtml", viewModel);
    }

    [HttpPost("AuditLog/CheckDriveActivity")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckDriveActivity(
        [FromServices] IDriveActivityMonitorService monitorService)
    {
        var currentUser = await GetCurrentUserAsync();

        try
        {
            var count = await monitorService.CheckForAnomalousActivityAsync();
            _logger.LogInformation("Board {UserId} triggered manual Drive activity check: {Count} anomalies",
                currentUser?.Id, count);

            SetSuccess(count > 0
                ? $"Drive activity check completed: {count} anomalous change(s) detected."
                : "Drive activity check completed: no anomalies detected.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual Drive activity check failed");
            SetError("Drive activity check failed. Check logs for details.");
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
        return GoogleSyncAuditView(
            $"Sync Audit: {resource.Name}",
            Url.Action(nameof(TeamController.Sync), "Team"),
            "Back to Sync Status",
            entries);
    }

}
