using Humans.Tickets.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Tickets.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Tickets.Controllers;

/// <summary>The shared gate account is provisioned on the first password set.</summary>
[Authorize(Policy = TicketsPolicies.Read)]
[Route("Tickets/Admin/Gate")]
internal sealed class TicketsGateAdminController(
    IUserService userService,
    GateTerminalAccountSeeder gateAccount) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var status = await gateAccount.GetStatusAsync();
        return View("~/Views/Tickets/Admin/Gate.cshtml", status);
    }

    [HttpPost("SetPassword")]
    [Authorize(Policy = TicketsPolicies.Manage)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPassword(string? password)
    {
        var actorId = GetCurrentUserId();
        if (actorId is null)
            return Challenge();

        if (string.IsNullOrWhiteSpace(password))
        {
            SetError("Enter a password.");
            return RedirectToAction(nameof(Index));
        }

        var result = await gateAccount.SetPasswordAsync(password, actorId.Value);
        if (!result.Succeeded)
        {
            SetError(string.Join(" ", result.Errors.Select(e => e.Description)));
            return RedirectToAction(nameof(Index));
        }

        SetSuccess("Gate terminal password set.");
        return RedirectToAction(nameof(Index));
    }
}
