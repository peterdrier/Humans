using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Humans.Application.Interfaces.Governance;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Humans.Application.Interfaces.Auth;

// RoleAssignment cross-domain nav properties (User, CreatedByUser) are [Obsolete] —
// RoleAssignmentService stitches them in memory from IUserService so controllers can
// continue to read them for view-model shaping. Nav-strip follow-up tracked in
// design-rules §15i.
#pragma warning disable CS0618

namespace Humans.Web.Controllers;

[Authorize]
[Route("[controller]")]
public class GovernanceController : HumansControllerBase
{
    private readonly IGovernanceIndexService _governanceIndexService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IClock _clock;

    public GovernanceController(
        UserManager<Domain.Entities.User> userManager,
        IGovernanceIndexService governanceIndexService,
        IRoleAssignmentService roleAssignmentService,
        IClock clock)
        : base(userManager)
    {
        _governanceIndexService = governanceIndexService;
        _roleAssignmentService = roleAssignmentService;
        _clock = clock;
    }

    public async Task<IActionResult> Index()
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        var data = await _governanceIndexService.GetIndexDataAsync(user.Id);

        var viewModel = new GovernanceIndexViewModel
        {
            StatutesContent = data.StatutesContent,
            HasApplication = data.HasApplication,
            ApplicationStatus = data.ApplicationStatus,
            ApplicationTier = data.ApplicationTier,
            ApplicationSubmittedAt = data.ApplicationSubmittedAt,
            ApplicationResolvedAt = data.ApplicationResolvedAt,
            ApplicationStatusBadgeClass = data.ApplicationStatus.GetBadgeClass(),
            CanApply = data.CanApply,
            IsApprovedColaborador = data.IsApprovedColaborador,
            ColaboradorCount = data.ColaboradorCount,
            AsociadoCount = data.AsociadoCount
        };

        return View(viewModel);
    }

    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
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
                UserEmail = ra.UserEmail ?? string.Empty,
                UserDisplayName = ra.UserDisplayName,
                RoleName = ra.RoleName,
                ValidFrom = ra.ValidFrom.ToDateTimeUtc(),
                ValidTo = ra.ValidTo?.ToDateTimeUtc(),
                Notes = ra.Notes,
                IsActive = ra.IsActive(now),
                CreatedByName = ra.CreatedByDisplayName,
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
