using System.Security.Claims;
using Humans.Base.Enums;
using Humans.Campaigns.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Campaigns.ViewComponents;

/// <summary>Renders only on the viewer's own profile: grants from Active or Completed campaigns.</summary>
public sealed class MyCodesViewComponent(ICampaignService campaigns) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode != ProfileCardViewMode.Self
            || !Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var viewerId)
            || viewerId != userId)
            return Content(string.Empty);

        var grants = await campaigns.GetActiveOrCompletedGrantsForUserAsync(userId, HttpContext.RequestAborted);
        if (grants.Count == 0) return Content(string.Empty);

        return View(grants);
    }
}
