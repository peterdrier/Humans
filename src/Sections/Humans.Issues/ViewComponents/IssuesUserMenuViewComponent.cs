using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Issues.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Issues.ViewComponents;

/// <summary>
/// The Issues link in the signed-in user menu, with its actionable-item count. No
/// IMemoryCache here (memory/code/viewcomponent-no-cache.md): IssuesService owns the
/// per-viewer count cache.
/// </summary>
internal sealed class IssuesUserMenuViewComponent(IIssuesService issuesService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var claim = UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(claim, out var currentUserId))
            return View(0);

        var roles = UserClaimsPrincipal.Claims
            .Where(c => string.Equals(c.Type, ClaimTypes.Role, StringComparison.Ordinal))
            .Select(c => c.Value)
            .ToList();
        var isAdmin = UserClaimsPrincipal.IsInRole(RoleNames.Admin);

        var count = await issuesService.GetActionableCountForViewerAsync(currentUserId, roles, isAdmin);
        return View(count);
    }
}
