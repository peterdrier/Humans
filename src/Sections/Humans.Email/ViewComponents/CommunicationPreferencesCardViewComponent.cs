using Humans.Base.Authorization;
using Humans.Base.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Email.ViewComponents;

/// <summary>
/// The read-only "Communication Preferences" card, contributed to <c>UserPartSlots.AdminDetail</c>
/// (Users decomposition commit 14; it was inline on <c>AdminDetail.cshtml</c>). Admin audience only:
/// renders for <see cref="ProfileCardViewMode.Admin"/> when the viewer passes the host page's
/// <see cref="PolicyNames.HumanAdminBoardOrAdmin"/> policy, and nothing for anyone else. The body is
/// <see cref="CommunicationPreferencesPanelViewComponent"/> in read-only mode (source + time per row).
/// </summary>
public sealed class CommunicationPreferencesCardViewComponent(IAuthorizationService authorization) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode != ProfileCardViewMode.Admin
            || !(await authorization.AuthorizeAsync(UserClaimsPrincipal, PolicyNames.HumanAdminBoardOrAdmin)).Succeeded)
            return Content(string.Empty);

        return View(userId);
    }
}
