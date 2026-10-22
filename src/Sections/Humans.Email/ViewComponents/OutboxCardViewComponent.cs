using Humans.Base.Authorization;
using Humans.Base.Enums;
using Humans.Email.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Email.ViewComponents;

/// <summary>
/// Renders only for an admin viewer who passes the host page's policy, even at zero messages; nothing for anyone else.
/// </summary>
public sealed class OutboxCardViewComponent(
    IEmailOutboxService outbox,
    IAuthorizationService authorization) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode != ProfileCardViewMode.Admin
            || !(await authorization.AuthorizeAsync(UserClaimsPrincipal, PolicyNames.HumanAdminBoardOrAdmin)).Succeeded)
            return Content(string.Empty);

        var count = await outbox.GetMessageCountForUserAsync(userId, HttpContext.RequestAborted);
        return View(new OutboxCardViewModel(userId, count));
    }
}

internal sealed record OutboxCardViewModel(Guid UserId, int MessageCount);
