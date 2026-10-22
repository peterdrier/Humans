using Humans.Base.Authorization;
using Humans.Base.Enums;
using Humans.Campaigns.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Campaigns.ViewComponents;

/// <summary>
/// Renders only for <see cref="ProfileCardViewMode.Admin"/> to a viewer passing the host page's
/// <see cref="PolicyNames.HumanAdminBoardOrAdmin"/>. Every grant, whatever the campaign's status.
/// </summary>
public sealed class CampaignCodesViewComponent(
    ICampaignService campaigns,
    IAuthorizationService authorization) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode != ProfileCardViewMode.Admin
            || !(await authorization.AuthorizeAsync(UserClaimsPrincipal, PolicyNames.HumanAdminBoardOrAdmin)).Succeeded)
            return Content(string.Empty);

        var grants = await campaigns.GetAllGrantsForUserAsync(userId, HttpContext.RequestAborted);
        if (grants.Count == 0) return Content(string.Empty);

        return View(grants);
    }
}
