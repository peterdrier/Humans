using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Web.Authorization;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.BoardOrAdmin)]
[Route("Board")]
public class BoardController : HumansControllerBase
{
    private readonly IAuditLogService _auditLogService;
    private readonly IOnboardingService _onboardingService;
    private readonly HumansDbContext _dbContext;

    public BoardController(
        IAuditLogService auditLogService,
        IOnboardingService onboardingService,
        UserManager<User> userManager,
        HumansDbContext dbContext)
        : base(userManager)
    {
        _auditLogService = auditLogService;
        _onboardingService = onboardingService;
        _dbContext = dbContext;
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
        var (items, totalCount, anomalyCount) = await _auditLogService.GetFilteredAsync(filter, page, pageSize);

        var entries = items.Select(e => new AuditLogEntryViewModel
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

        // Batch-load display names for all user IDs (actors + subjects)
        var allUserIds = entries
            .SelectMany(e => new[] { e.ActorUserId, e.SubjectUserId })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var userDisplayNames = allUserIds.Count > 0
            ? await _dbContext.Users.AsNoTracking()
                .Where(u => allUserIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName)
            : new Dictionary<Guid, string>();

        // Batch-load team names for target teams
        var teamIds = entries
            .Where(e => e.TargetTeamId.HasValue)
            .Select(e => e.TargetTeamId!.Value)
            .Distinct()
            .ToList();

        var teamNames = teamIds.Count > 0
            ? await _dbContext.Teams.AsNoTracking()
                .Where(t => teamIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => (t.Name, t.Slug))
            : new Dictionary<Guid, (string Name, string Slug)>();

        var viewModel = new AuditLogListViewModel
        {
            Entries = entries,
            ActionFilter = filter,
            AnomalyCount = anomalyCount,
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize,
            UserDisplayNames = userDisplayNames,
            TeamNames = teamNames
        };

        return View("~/Views/Shared/AuditLog.cshtml", viewModel);
    }
}
