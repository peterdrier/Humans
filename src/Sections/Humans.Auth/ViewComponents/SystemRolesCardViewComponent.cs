using Humans.Users.Contracts;
using Humans.Auth.Contracts;
using Humans.Auth.Models;
using Humans.Base.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Auth.ViewComponents;

/// <summary>
/// Renders only in Admin view mode for a viewer passing the host page's
/// <see cref="UsersPolicies.Read"/> policy; then always, even when the member holds no role.
/// </summary>
public sealed class SystemRolesCardViewComponent(
    ISystemRoleService systemRoles,
    IAuthorizationService authorization,
    IClock clock) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode != ProfileCardViewMode.Admin
            || !(await authorization.AuthorizeAsync(UserClaimsPrincipal, UsersPolicies.Read)).Succeeded)
            return Content(string.Empty);

        var now = clock.GetCurrentInstant();
        var assignments = await systemRoles.GetByUserIdAsync(userId, HttpContext.RequestAborted);

        return View(new SystemRolesCardViewModel(
            userId,
            [.. assignments.Select(ra => new AdminSystemRoleViewModel
            {
                Id = ra.Id,
                UserId = ra.UserId,
                RoleName = ra.RoleName,
                ValidFrom = ra.ValidFrom.ToDateTimeUtc(),
                ValidTo = ra.ValidTo?.ToDateTimeUtc(),
                Notes = ra.Notes,
                IsActive = ra.IsActive(now)
            })]));
    }
}

internal sealed record SystemRolesCardViewModel(Guid UserId, IReadOnlyList<AdminSystemRoleViewModel> Roles);
