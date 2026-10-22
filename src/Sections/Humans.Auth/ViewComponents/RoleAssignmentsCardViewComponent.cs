using Humans.Auth.Contracts;
using Humans.Auth.Models;
using Humans.Base.Authorization;
using Humans.Base.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Auth.ViewComponents;

/// <summary>
/// Renders only in Admin view mode for a viewer passing the host page's
/// <see cref="PolicyNames.HumanAdminBoardOrAdmin"/> policy; then always, even when the member holds no role.
/// </summary>
public sealed class RoleAssignmentsCardViewComponent(
    IRoleAssignmentService roleAssignments,
    IAuthorizationService authorization,
    IClock clock) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode != ProfileCardViewMode.Admin
            || !(await authorization.AuthorizeAsync(UserClaimsPrincipal, PolicyNames.HumanAdminBoardOrAdmin)).Succeeded)
            return Content(string.Empty);

        var now = clock.GetCurrentInstant();
        var assignments = await roleAssignments.GetByUserIdAsync(userId, HttpContext.RequestAborted);

        return View(new RoleAssignmentsCardViewModel(
            userId,
            [.. assignments.Select(ra => new AdminRoleAssignmentViewModel
            {
                Id = ra.Id,
                UserId = ra.UserId,
                RoleName = ra.RoleName,
                ValidFrom = ra.ValidFrom.ToDateTimeUtc(),
                ValidTo = ra.ValidTo?.ToDateTimeUtc(),
                Notes = ra.Notes,
                IsActive = ra.IsActive(now),
                CreatedAt = ra.CreatedAt.ToDateTimeUtc()
            })]));
    }
}

internal sealed record RoleAssignmentsCardViewModel(Guid UserId, IReadOnlyList<AdminRoleAssignmentViewModel> Roles);
