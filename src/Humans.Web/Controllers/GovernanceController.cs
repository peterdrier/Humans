using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Humans.Application.Interfaces.Governance;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Humans.Application.Interfaces.Auth;

#pragma warning disable CS0618 // RoleAssignment.User/CreatedByUser — stitched in-memory by RoleAssignmentService (§15i).

using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

[Authorize]
[Route("[controller]")]
public class GovernanceController(
    IUserService userService,
    IGovernanceIndexService governanceIndexService,
    IRoleAssignmentService roleAssignmentService,
    IClock clock) : HumansControllerBase(userService)
{
    public async Task<IActionResult> Index()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        var data = await governanceIndexService.GetIndexDataAsync(user.Id);

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
        var now = clock.GetCurrentInstant();

        var (assignments, totalCount) = await roleAssignmentService.GetFilteredAsync(
            role, activeOnly: !showInactive, page, pageSize, now);

        var viewModel = new AdminRoleAssignmentListViewModel
        {
            RoleAssignments = assignments.Select(ra => new AdminRoleAssignmentViewModel
            {
                Id = ra.Id,
                UserId = ra.UserId,
                UserEmail = ra.UserEmail ?? string.Empty,
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
