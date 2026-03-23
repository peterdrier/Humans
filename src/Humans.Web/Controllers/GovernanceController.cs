using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Humans.Application;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Extensions;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize]
[Route("[controller]")]
public class GovernanceController : HumansControllerBase
{
    private readonly ILogger<GovernanceController> _logger;
    private readonly ILegalDocumentService _legalDocService;
    private readonly IProfileService _profileService;
    private readonly IApplicationDecisionService _applicationDecisionService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IClock _clock;

    public GovernanceController(
        UserManager<Domain.Entities.User> userManager,
        ILogger<GovernanceController> logger,
        ILegalDocumentService legalDocService,
        IProfileService profileService,
        IApplicationDecisionService applicationDecisionService,
        IRoleAssignmentService roleAssignmentService,
        IClock clock)
        : base(userManager)
    {
        _logger = logger;
        _legalDocService = legalDocService;
        _profileService = profileService;
        _applicationDecisionService = applicationDecisionService;
        _roleAssignmentService = roleAssignmentService;
        _clock = clock;
    }

    public async Task<IActionResult> Index()
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        var applications = await _applicationDecisionService.GetUserApplicationsAsync(user.Id);
        var latestApplication = applications.Count > 0 ? applications[0] : null;

        var statutesContent = await _legalDocService.GetDocumentContentAsync("statutes");

        // Tier member counts for the sidebar
        var (colaboradorCount, asociadoCount) = await _profileService.GetTierCountsAsync();

        var viewModel = new GovernanceIndexViewModel
        {
            StatutesContent = statutesContent,
            HasApplication = latestApplication is not null,
            ApplicationStatus = latestApplication?.Status,
            ApplicationSubmittedAt = latestApplication?.SubmittedAt.ToDateTimeUtc(),
            ApplicationResolvedAt = latestApplication?.ResolvedAt?.ToDateTimeUtc(),
            ApplicationStatusBadgeClass = latestApplication?.Status.GetBadgeClass(),
            CanApply = latestApplication is null ||
                latestApplication.Status != ApplicationStatus.Submitted,
            ColaboradorCount = colaboradorCount,
            AsociadoCount = asociadoCount
        };

        return View(viewModel);
    }

    [Authorize(Roles = RoleGroups.BoardOrAdmin)]
    [HttpGet("Roles")]
    public async Task<IActionResult> Roles(string? role, bool showInactive = false, int page = 1)
    {
        var pageSize = 50;
        var now = _clock.GetCurrentInstant();

        var (assignments, totalCount) = await _roleAssignmentService.GetFilteredAsync(
            role, activeOnly: !showInactive, page, pageSize, now);

        var viewModel = new AdminRoleAssignmentListViewModel
        {
            RoleAssignments = assignments.Select(ra => new AdminRoleAssignmentViewModel
            {
                Id = ra.Id,
                UserId = ra.UserId,
                UserEmail = ra.User.Email ?? string.Empty,
                UserDisplayName = ra.User.DisplayName,
                RoleName = ra.RoleName,
                ValidFrom = ra.ValidFrom.ToDateTimeUtc(),
                ValidTo = ra.ValidTo?.ToDateTimeUtc(),
                Notes = ra.Notes,
                IsActive = ra.IsActive(now),
                CreatedByName = ra.CreatedByUser?.DisplayName,
                CreatedAt = ra.CreatedAt.ToDateTimeUtc()
            }).ToList(),
            RoleFilter = role,
            ShowInactive = showInactive,
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize
        };

        return View("~/Views/Shared/Roles.cshtml", viewModel);
    }
}
