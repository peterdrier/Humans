using Humans.Auth.Authorization;
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
/// The class gate lets Board read the roster; <c>AddRole</c> and <c>RevokeRole</c> narrow to <see cref="UsersPolicies.Manage"/>
/// (the principals <c>SystemRoleRules</c> admits) and add the per-role <see cref="PolicyNames.SystemRoleManage"/> check.
/// </summary>
[Authorize(Policy = UsersPolicies.Read)]
internal sealed class SystemRolesController(
    IUserService userService,
    ISystemRoleService systemRoleService,
    ISystemRoleManagement systemRoleManagement,
    IClock clock,
    IAuthorizationService authorizationService,
    RoleCatalog catalog,
    ILogger<SystemRolesController> logger,
    IStringLocalizer<SharedResource> localizer) : HumansControllerBase(userService)
{
    // Admin-only page: English per memory/code/localization-admin-exempt.md.
    private const string NotesTooLongMessage = "Notes must be 2,000 characters or fewer.";

    [HttpGet("Auth/Admin/Roles")]
    public async Task<IActionResult> Roles(string? role, bool showInactive = false, int page = 1)
    {
        var pageSize = 50;
        var now = clock.GetCurrentInstant();

        var (assignments, totalCount) = await systemRoleService.GetFilteredAsync(
            role, activeOnly: !showInactive, page, pageSize, now, HttpContext.RequestAborted);

        var viewModel = new AdminSystemRoleListViewModel
        {
            SystemRoles = assignments.Select(ra => new AdminSystemRoleViewModel
            {
                Id = ra.Id,
                UserId = ra.UserId,
                UserEmail = ra.UserEmail ?? string.Empty,
                RoleName = ra.RoleName,
                ValidFrom = ra.ValidFrom.ToDateTimeUtc(),
                ValidTo = ra.ValidTo?.ToDateTimeUtc(),
                Notes = ra.Notes,
                IsActive = ra.IsActive(now)
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
    [Authorize(Policy = UsersPolicies.Manage)]
    public async Task<IActionResult> AddRole(Guid id)
    {
        if (await UserService.GetRawUserInfoAsync(id, HttpContext.RequestAborted) is null)
            return NotFound();

        var viewModel = new CreateSystemRoleViewModel
        {
            UserId = id,
            AvailableRoles = SystemRoleRules.Assignable(User, catalog)
        };

        return View(viewModel);
    }

    [HttpPost("Auth/Admin/{id:guid}/Roles/Add")]
    [Authorize(Policy = UsersPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddRole(Guid id, CreateSystemRoleViewModel model)
    {
        if (await UserService.GetRawUserInfoAsync(id, HttpContext.RequestAborted) is null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(model.RoleName))
        {
            ModelState.AddModelError(nameof(model.RoleName), "Please select a role.");
            model.UserId = id;
            model.AvailableRoles = SystemRoleRules.Assignable(User, catalog);
            return View(model);
        }

        if (SystemRoleRules.IsAddedInGovernance(model.RoleName))
        {
            SetError(BoardInGovernanceMessage);
            return RedirectToAction("AdminDetail", "UsersAdmin", new { id });
        }

        var currentUser = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (currentUser is null)
        {
            return Unauthorized();
        }

        var authResult = await authorizationService.AuthorizeAsync(
            User, model.RoleName, PolicyNames.SystemRoleManage);
        if (!authResult.Succeeded)
        {
            logger.LogWarning(
                "Authorization denied for system role: principal {Principal} attempted to assign role {Role} to user {UserId}",
                User.Identity?.Name, model.RoleName, id);
            return Forbid();
        }

        var result = await systemRoleManagement.AssignRoleAsync(
            id, model.RoleName, currentUser.Id, model.Notes);

        if (string.Equals(result.ErrorKey, "UserUnavailable", StringComparison.Ordinal))
            return NotFound();

        if (result.Success)
            SetSuccess(string.Format(localizer["Admin_RoleAssigned"].Value, model.RoleName));
        else if (string.Equals(result.ErrorKey, "NotesTooLong", StringComparison.Ordinal))
            SetError(NotesTooLongMessage);
        else
            SetError(string.Format(localizer["Admin_RoleAlreadyActive"].Value, model.RoleName));

        return RedirectToAction("AdminDetail", "UsersAdmin", new { id });
    }

    private const string BoardInGovernanceMessage =
        "Board seats are added and ended on the Board page in Governance (/Governance/Board).";

    [HttpPost("Auth/Admin/{id:guid}/Roles/{roleId:guid}/Revoke")]
    [Authorize(Policy = UsersPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeRole(Guid id, Guid roleId, string? notes)
    {
        var systemRole = await systemRoleManagement.GetByIdAsync(roleId, HttpContext.RequestAborted);

        if (systemRole is null)
        {
            return NotFound();
        }

        // Ending a seat here would skip Governance's Board office cleanup and its Google sync request.
        if (SystemRoleRules.IsAddedInGovernance(systemRole.RoleName))
        {
            SetError(BoardInGovernanceMessage);
            return RedirectToAction("AdminDetail", "UsersAdmin", new { id = systemRole.UserId });
        }

        var currentUser = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (currentUser is null)
        {
            return Unauthorized();
        }

        var authResult = await authorizationService.AuthorizeAsync(
            User, systemRole.RoleName, PolicyNames.SystemRoleManage);
        if (!authResult.Succeeded)
        {
            logger.LogWarning(
                "Authorization denied for revoking role: principal {Principal} attempted to revoke role {Role} for user {UserId}",
                User.Identity?.Name, systemRole.RoleName, systemRole.UserId);
            return NotFound();
        }

        var result = await systemRoleManagement.RevokeRoleAsync(
            roleId, currentUser.Id, notes);

        if (result.Success)
            SetSuccess(string.Format(
                localizer["Admin_RoleRevoked"].Value,
                systemRole.RoleName,
                systemRole.UserDisplayName));
        else if (string.Equals(result.ErrorKey, "NotesTooLong", StringComparison.Ordinal))
            SetError(NotesTooLongMessage);
        else
            SetError(localizer["Admin_RoleNotActive"].Value);

        return RedirectToAction("AdminDetail", "UsersAdmin", new { id = systemRole.UserId });
    }
}
