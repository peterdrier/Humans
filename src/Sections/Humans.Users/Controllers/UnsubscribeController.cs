using Humans.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;
using Humans.Users.Services;

namespace Humans.Users.Controllers;

internal sealed class UnsubscribeController(IUnsubscribeService unsubscribeService, ILogger<UnsubscribeController> logger)
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
            return RedirectToAction(
                // GuestController is Shell; Humans.Users cannot reference Humans.Web, so the action and
                // controller names are literals here (design §15, Users gotcha 7).
                "CommunicationPreferences", "Guest",
                new { utoken = token });
        }

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
            return RedirectToAction(
                // GuestController is Shell; Humans.Users cannot reference Humans.Web, so the action and
                // controller names are literals here (design §15, Users gotcha 7).
                "CommunicationPreferences", "Guest",
                new { utoken = token });
        }

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
