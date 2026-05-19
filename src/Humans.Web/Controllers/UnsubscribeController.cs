using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

public class UnsubscribeController(IUnsubscribeService unsubscribeService, ILogger<UnsubscribeController> logger)
    : Controller
{
    [HttpGet("/Unsubscribe/{token:minlength(40)}")]
    public async Task<IActionResult> Index(string token)
    {
        var result = await unsubscribeService.ValidateTokenAsync(token);

        if (result.IsExpired)
            return View("Expired");

        if (!result.IsValid)
            return NotFound();

        if (!result.IsLegacy)
        {
            // New tokens redirect to comms preferences (no session).
            return RedirectToAction(
                nameof(GuestController.CommunicationPreferences), "Guest",
                new { utoken = token });
        }

        // Legacy: show confirmation page.
        ViewData["DisplayName"] = result.DisplayName;
        ViewData["CategoryName"] = MessageCategory.Marketing.ToDisplayName();
        return View();
    }

    [HttpPost("/Unsubscribe/{token:minlength(40)}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(string token)
    {
        var result = await unsubscribeService.ConfirmUnsubscribeAsync(token, "MagicLink");

        if (result.IsExpired)
            return View("Expired");

        if (!result.IsValid)
            return NotFound();

        if (!result.IsLegacy)
        {
            // New tokens redirect to comms preferences (no session).
            return RedirectToAction(
                nameof(GuestController.CommunicationPreferences), "Guest",
                new { utoken = token });
        }

        // Legacy: show done page.
        ViewData["CategoryName"] = MessageCategory.Marketing.ToDisplayName();
        return View("Done");
    }

    // RFC 8058 one-click — POSTed by email clients via List-Unsubscribe header; no AF token.
    [HttpPost("/Unsubscribe/OneClick")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> OneClick([FromQuery] string token)
    {
        try
        {
            var result = await unsubscribeService.ConfirmUnsubscribeAsync(token, "OneClick");
            if (!result.IsValid)
                return BadRequest();

            logger.LogInformation(
                "RFC 8058 one-click unsubscribe: user {UserId} from {Category}",
                result.UserId, result.Category);
            return Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process RFC 8058 one-click unsubscribe");
            return StatusCode(500);
        }
    }
}
