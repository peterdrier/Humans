using Humans.Users.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Enums;
using Humans.Campaigns.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Campaigns.ViewComponents;

/// <summary>
/// Renders only for <see cref="ProfileCardViewMode.Admin"/> to a viewer passing the host page's
/// <see cref="UsersPolicies.Read"/>. Every grant, whatever the campaign's status.
/// </summary>
internal sealed class CampaignCodesViewComponent(
    ICampaignManagement campaigns,
    IAuthorizationService authorization) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode != ProfileCardViewMode.Admin
            || !(await authorization.AuthorizeAsync(UserClaimsPrincipal, UsersPolicies.Read)).Succeeded)
            return Content(string.Empty);

        var grants = await campaigns.GetAllGrantsForUserAsync(userId, HttpContext.RequestAborted);
        if (grants.Count == 0) return Content(string.Empty);

        return View(grants);
    }
}
