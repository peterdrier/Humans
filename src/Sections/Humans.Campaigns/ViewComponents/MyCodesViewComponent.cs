using Humans.Base.Extensions;
using Humans.Base.Enums;
using Humans.Campaigns.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Campaigns.ViewComponents;

/// <summary>Renders only on the viewer's own profile: grants from Active or Completed campaigns.</summary>
internal sealed class MyCodesViewComponent(ICampaignManagement campaigns) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode != ProfileCardViewMode.Self
            || UserClaimsPrincipal.GetUserId() is not { } viewerId
            || viewerId != userId)
            return Content(string.Empty);

        var grants = await campaigns.GetGrantsForUserAsync(userId, HttpContext.RequestAborted);
        if (grants.Count == 0) return Content(string.Empty);

        return View(grants);
    }
}
