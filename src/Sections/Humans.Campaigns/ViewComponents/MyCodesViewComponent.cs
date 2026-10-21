using System.Security.Claims;
using Humans.Base.Enums;
using Humans.Campaigns.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Campaigns.ViewComponents;

/// <summary>
/// The "My Codes" card, contributed to <c>UserPartSlots.Profile</c>. Own profile only: renders
/// for <see cref="ProfileCardViewMode.Self"/> when the signed-in user is the target, and nothing
/// for anyone else. Shows the viewer's grants from Active or Completed campaigns; nothing when
/// there are none.
/// </summary>
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
