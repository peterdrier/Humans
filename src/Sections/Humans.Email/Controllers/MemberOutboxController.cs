using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Email.Controllers;

/// <summary>
/// The per-member outbox history pages: a member's own at <c>/Email/Outbox</c> and an
/// admin's view of any member's at <c>/Email/Admin/{id}/Outbox</c>. Both read
/// <see cref="IEmailOutboxService"/> and render <c>Views/MemberOutbox/Outbox.cshtml</c>.
/// </summary>
/// <remarks>
/// The admin card that links here is Email's <c>OutboxCardViewComponent</c> user part.
/// </remarks>
[Authorize]
internal sealed class MemberOutboxController(
    IUserService userService,
    IEmailOutboxService outbox) : HumansControllerBase(userService)
{
    /// <summary>The signed-in member's own outbox history. Never takes a user id.</summary>
    [HttpGet("Email/Outbox")]
    public async Task<IActionResult> MyOutbox()
    {
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null)
            return NotFound();

        var messages = await outbox.GetMessagesForUserAsync(user.Id, HttpContext.RequestAborted);
        return View("Outbox", messages);
    }

    /// <summary>Any member's outbox history, for HumanAdmin, Board and Admin.</summary>
    [Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
    [HttpGet("Email/Admin/{id:guid}/Outbox")]
    public async Task<IActionResult> AdminOutbox(Guid id, CancellationToken ct)
    {
        var messages = await outbox.GetMessagesForUserAsync(id, ct);

        ViewBag.HumanId = id;
        return View("Outbox", messages);
    }
}
