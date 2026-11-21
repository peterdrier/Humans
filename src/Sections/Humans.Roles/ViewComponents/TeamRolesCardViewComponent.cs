using Humans.Base.Authorization;
using Humans.Base.Extensions;
using Humans.Roles.Models;
using Humans.Roles.Services;
using Humans.Teams.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Roles.ViewComponents;

/// <summary>
/// A team's roles on the team page. Parameters bind from <see cref="TeamRolesCardArgs"/>. Renders nothing when there is
/// nothing to see and nothing to manage.
/// </summary>
internal sealed class TeamRolesCardViewComponent(IRoleManagementService roles) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid teamId)
    {
        var owner = new Owner(TeamOwner.Kind, teamId);
        if (UserClaimsPrincipal.GetUserId() is not { } userId) return Content(string.Empty);

        var ct = HttpContext.RequestAborted;
        if ((await roles.GetOwnersAsync(ct)).FirstOrDefault(o => o.Owner == owner) is not { } view) return Content(string.Empty);

        var canManage = await roles.CanManageAsync(owner, userId, ct);
        var rows = (await roles.GetVisibleRolesAsync(userId, ct))
            .Where(r => r.Owner == owner)
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new RoleRow(r, Url.Action("Role", "Roles", new { kind = owner.Kind, key = view.Key, slug = r.CustomSlug ?? r.Slug })!))
            .ToList();
        if (rows.Count == 0 && !canManage) return Content(string.Empty);

        return View(new RolesCardViewModel(rows, canManage, Url.Action("Owner", "Roles", new { kind = owner.Kind, key = view.Key })!));
    }
}
