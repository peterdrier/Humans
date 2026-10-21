using Humans.Email.Services;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Email.Controllers;

/// <summary>
/// The unsubscribe links in every outbound email (<c>/Unsubscribe/{token}</c> and the RFC 8058
/// one-click <c>/Unsubscribe/OneClick</c>). Moved from Users in Users decomposition commit 14 with the
/// same URLs; tokens minted before the move still validate (same Data Protection purposes and payload).
/// </summary>
internal sealed class UnsubscribeController(IUnsubscribeService unsubscribeService, ILogger<UnsubscribeController> logger)
    : Controller
{
    [HttpGet("/Unsubscribe/{token:minlength(40)}")]
    public async Task<IActionResult> Index(string token)
    {
        var result = await unsubscribeService.ValidateTokenAsync(token, HttpContext.RequestAborted);

        if (result.IsExpired)
            return View("Expired");

        if (!result.IsValid)
            return NotFound();

        if (!result.IsLegacy)
        {
            return RedirectToAction(
                "CommunicationPreferences", "GuestAccount",
                new { utoken = token });
        }

        ViewData["DisplayName"] = result.DisplayName;
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
                "CommunicationPreferences", "GuestAccount",
                new { utoken = token });
        }

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
