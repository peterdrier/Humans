using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.BoardOrAdmin)]
[Route("Board")]
public class BoardController : HumansControllerBase
{
    private readonly IAuditLogService _auditLogService;
    private readonly IOnboardingService _onboardingService;

    public BoardController(
        IAuditLogService auditLogService,
        IOnboardingService onboardingService,
        UserManager<User> userManager)
        : base(userManager)
    {
        _auditLogService = auditLogService;
        _onboardingService = onboardingService;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var dashboardData = await _onboardingService.GetAdminDashboardAsync();
        var recentEntries = await _auditLogService.GetRecentAsync(15);

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
        var result = await _auditLogService.GetAuditLogPageAsync(filter, page, pageSize);

        var entries = result.Items.Select(e => new AuditLogEntryViewModel
        {
            Action = e.Action,
            Description = e.Description,
            OccurredAt = e.OccurredAt.ToDateTimeUtc(),
            ActorUserId = e.ActorUserId,
            IsSystemAction = e.ActorUserId is null,
            EntityType = e.EntityType,
            EntityId = e.EntityId,
            RelatedEntityType = e.RelatedEntityType,
            RelatedEntityId = e.RelatedEntityId
        }).ToList();

        var viewModel = new AuditLogListViewModel
        {
            Entries = entries,
            ActionFilter = filter,
            AnomalyCount = result.AnomalyCount,
            TotalCount = result.TotalCount,
            PageNumber = page,
            PageSize = pageSize,
            UserDisplayNames = result.UserDisplayNames,
            TeamNames = result.TeamNames
        };

        return View("~/Views/Shared/AuditLog.cshtml", viewModel);
    }
}
