using Humans.Auth.Contracts;
using Humans.Auth.Models;
using Humans.Auth.Services;
using Humans.Base;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Auth.Controllers;

/// <summary>
/// The role-assignment admin pages: the system-wide roster at <c>/Auth/Admin/Roles</c> and
/// assign / end on a member at <c>/Auth/Admin/{id}/Roles/...</c>. The per-member card on the
/// admin human-detail page is this section's <c>RoleAssignmentsCardViewComponent</c> user part.
/// </summary>
/// <remarks>
/// The class gate is <see cref="PolicyNames.HumanAdminBoardOrAdmin"/>; <c>AddRole</c> and
/// <c>EndRole</c> add the resource-based <see cref="PolicyNames.RoleAssignmentManage"/> check per
/// role name. The audit entries are written by <see cref="IRoleAssignmentManagement"/>.
/// </remarks>
[Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
internal sealed class RoleAssignmentsController(
    IUserService userService,
    IRoleAssignmentService roleAssignmentService,
    IRoleAssignmentManagement roleAssignmentManagement,
    IClock clock,
    IAuthorizationService authorizationService,
    ILogger<RoleAssignmentsController> logger,
    IStringLocalizer<SharedResource> localizer) : HumansControllerBase(userService)
{
    [HttpGet("Auth/Admin/Roles")]
    public async Task<IActionResult> Roles(string? role, bool showInactive = false, int page = 1)
    {
        var pageSize = 50;
        var now = clock.GetCurrentInstant();

        var (assignments, totalCount) = await roleAssignmentService.GetFilteredAsync(
            role, activeOnly: !showInactive, page, pageSize, now, HttpContext.RequestAborted);

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

        return View(viewModel);
    }

    [HttpGet("Auth/Admin/{id:guid}/Roles/Add")]
    public async Task<IActionResult> AddRole(Guid id)
    {
        if (await UserService.GetRawUserInfoAsync(id, HttpContext.RequestAborted) is null)
            return NotFound();

        var viewModel = new CreateRoleAssignmentViewModel
        {
            UserId = id,
            AvailableRoles = [.. RoleChecks.GetAssignableRoles(User)]
        };

        return View(viewModel);
    }

    [HttpPost("Auth/Admin/{id:guid}/Roles/Add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddRole(Guid id, CreateRoleAssignmentViewModel model)
    {
        if (await UserService.GetRawUserInfoAsync(id) is null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(model.RoleName))
        {
            ModelState.AddModelError(nameof(model.RoleName), "Please select a role.");
            model.UserId = id;
            model.AvailableRoles = [.. RoleChecks.GetAssignableRoles(User)];
            return View(model);
        }

        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
        {
            return Unauthorized();
        }

        var authResult = await authorizationService.AuthorizeAsync(
            User, model.RoleName, PolicyNames.RoleAssignmentManage);
        if (!authResult.Succeeded)
        {
            logger.LogWarning(
                "Authorization denied for role assignment: principal {Principal} attempted to assign role {Role} to user {UserId}",
                User.Identity?.Name, model.RoleName, id);
            return Forbid();
        }

        var result = await roleAssignmentManagement.AssignRoleAsync(
            id, model.RoleName, currentUser.Id, model.Notes);

        if (result.Success)
            SetSuccess(string.Format(localizer["Admin_RoleAssigned"].Value, model.RoleName));
        else
            SetError(string.Format(localizer["Admin_RoleAlreadyActive"].Value, model.RoleName));

        return RedirectToAction("AdminDetail", "UsersAdmin", new { id });
    }

    [HttpPost("Auth/Admin/{id:guid}/Roles/{roleId:guid}/End")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EndRole(Guid id, Guid roleId, string? notes)
    {
        var roleAssignment = await roleAssignmentService.GetByIdAsync(roleId);

        if (roleAssignment is null)
        {
            return NotFound();
        }

        var currentUser = await GetCurrentUserInfoAsync();
        if (currentUser is null)
        {
            return Unauthorized();
        }

        var authResult = await authorizationService.AuthorizeAsync(
            User, roleAssignment.RoleName, PolicyNames.RoleAssignmentManage);
        if (!authResult.Succeeded)
        {
            logger.LogWarning(
                "Authorization denied for ending role: principal {Principal} attempted to end role {Role} for user {UserId}",
                User.Identity?.Name, roleAssignment.RoleName, roleAssignment.UserId);
            return NotFound();
        }

        var result = await roleAssignmentManagement.EndRoleAsync(
            roleId, currentUser.Id, notes);

        if (result.Success)
            SetSuccess(string.Format(
                localizer["Admin_RoleEnded"].Value,
                roleAssignment.RoleName,
                roleAssignment.UserDisplayName));
        else
            SetError(localizer["Admin_RoleNotActive"].Value);

        return RedirectToAction("AdminDetail", "UsersAdmin", new { id = roleAssignment.UserId });
    }
}
