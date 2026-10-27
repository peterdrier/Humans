using Humans.Email.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Email.Controllers;

[Route("Email/Unsubscribe")]
// Reached from an email link; the token is the credential.
[AllowAnonymous]
internal sealed class UnsubscribeController(IUnsubscribeService unsubscribeService, ILogger<UnsubscribeController> logger)
    : Controller
{
    [HttpGet("{token:minlength(40)}")]
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

    [HttpPost("{token:minlength(40)}")]
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

    // RFC 8058 one-click: email clients POST here from the List-Unsubscribe header, so there is no anti-forgery token.
    [HttpPost("OneClick")]
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
