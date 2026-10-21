using Humans.Base.Authorization;
using Humans.Base.Enums;
using Humans.Email.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Email.ViewComponents;

/// <summary>
/// The "Email Outbox" card, contributed to <c>UserPartSlots.AdminDetail</c>. Admin audience
/// only: renders for <see cref="ProfileCardViewMode.Admin"/> when the viewer passes the same
/// <see cref="PolicyNames.HumanAdminBoardOrAdmin"/> policy as the host page, and nothing for
/// anyone else. Shows the member's message count and links to
/// <c>MemberOutboxController.AdminOutbox</c>; always rendered for an admin, even at zero.
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

/// <summary>What the Email Outbox card renders.</summary>
internal sealed record OutboxCardViewModel(Guid UserId, int MessageCount);
