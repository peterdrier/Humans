using Humans.Application.Interfaces.Users;
using Humans.Web.Authorization;
using Humans.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>
/// Gate-terminal credential management: ticket admins set/rotate the shared
/// gate account's password here (the account is provisioned on first set).
/// The terminal signs in at /Account/GateLogin and uses /Scanner/Tickets.
/// </summary>
[Authorize(Policy = PolicyNames.TicketAdminOrAdmin)]
[Route("Tickets/Admin/Gate")]
public sealed class TicketsGateAdminController(
    IUserServiceRead userService,
    GateTerminalAccountSeeder gateAccount) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var status = await gateAccount.GetStatusAsync();
        return View("~/Views/Tickets/Admin/Gate.cshtml", status);
    }

    [HttpPost("SetPassword")]
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
