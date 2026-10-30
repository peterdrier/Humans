using Humans.Users.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Email.ViewComponents;

/// <summary>Renders only for an admin viewer who passes the host page's policy; nothing for anyone else.</summary>
internal sealed class CommunicationPreferencesCardViewComponent(IAuthorizationService authorization) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode != ProfileCardViewMode.Admin
            || !(await authorization.AuthorizeAsync(UserClaimsPrincipal, UsersPolicies.Read)).Succeeded)
            return Content(string.Empty);

        return View(userId);
    }
}
