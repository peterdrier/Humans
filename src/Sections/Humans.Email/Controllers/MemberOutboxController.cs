using Humans.Base.Attributes;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Email.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Email.Controllers;

[Authorize]
internal sealed class MemberOutboxController(
    IUserService userService,
    IEmailOutboxService outbox) : HumansControllerBase(userService)
{
    /// <summary>Never takes a user id.</summary>
    [HttpGet("Email/Outbox")]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> MyOutbox()
    {
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null)
            return NotFound();

        var messages = await outbox.GetMessagesForUserAsync(user.Id, HttpContext.RequestAborted);
        return View("Outbox", messages);
    }

    [Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
    [HttpGet("Email/Admin/{id:guid}/Outbox")]
    public async Task<IActionResult> AdminOutbox(Guid id, CancellationToken ct)
    {
        var messages = await outbox.GetMessagesForUserAsync(id, ct);

        ViewBag.HumanId = id;
        return View("Outbox", messages);
    }
}
