using Humans.Users.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Enums;
using Humans.Email.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Email.ViewComponents;

/// <summary>
/// Renders only for an admin viewer who passes the host page's policy, even at zero messages; nothing for anyone else.
/// </summary>
internal sealed class OutboxCardViewComponent(
    IEmailOutboxService outbox,
    IAuthorizationService authorization) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode != ProfileCardViewMode.Admin
            || !(await authorization.AuthorizeAsync(UserClaimsPrincipal, UsersPolicies.Read)).Succeeded)
            return Content(string.Empty);

        var count = await outbox.GetMessageCountForUserAsync(userId, HttpContext.RequestAborted);
        return View(new OutboxCardViewModel(userId, count));
    }
}

internal sealed record OutboxCardViewModel(Guid UserId, int MessageCount);
