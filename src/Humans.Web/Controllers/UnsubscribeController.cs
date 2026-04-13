using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

public class UnsubscribeController : Controller
{
    private readonly IUnsubscribeService _unsubscribeService;
    private readonly ILogger<UnsubscribeController> _logger;

    public UnsubscribeController(
        IUnsubscribeService unsubscribeService,
        ILogger<UnsubscribeController> logger)
    {
        _unsubscribeService = unsubscribeService;
        _logger = logger;
    }

    [HttpGet("/Unsubscribe/{token}")]
    public async Task<IActionResult> Index(string token)
    {
        var result = await _unsubscribeService.ValidateTokenAsync(token);

        if (result.IsExpired)
            return View("Expired");

        if (!result.IsValid)
            return NotFound();

        if (!result.IsLegacy)
        {
            // New tokens redirect to comms preferences with token — no session created
            return RedirectToAction(
                nameof(GuestController.CommunicationPreferences), "Guest",
                new { utoken = token });
        }

        // Legacy tokens show the unsubscribe confirmation page
        ViewData["DisplayName"] = result.DisplayName;
        ViewData["CategoryName"] = MessageCategory.Marketing.ToDisplayName();
        return View();
    }

    [HttpPost("/Unsubscribe/{token}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(string token)
    {
        var result = await _unsubscribeService.ConfirmUnsubscribeAsync(token, "MagicLink");

        if (result.IsExpired)
            return View("Expired");

        if (!result.IsValid)
            return NotFound();

        if (!result.IsLegacy)
        {
            // New tokens redirect to comms preferences with token — no session created
            return RedirectToAction(
                nameof(GuestController.CommunicationPreferences), "Guest",
                new { utoken = token });
        }

        // Legacy tokens show the done page
        ViewData["CategoryName"] = MessageCategory.Marketing.ToDisplayName();
        return View("Done");
    }

    /// <summary>
    /// RFC 8058 one-click unsubscribe endpoint.
    /// Email clients POST List-Unsubscribe=One-Click to the URL in the List-Unsubscribe header,
    /// which includes the token as a query parameter. No anti-forgery token required.
    /// </summary>
    [HttpPost("/Unsubscribe/OneClick")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> OneClick([FromQuery] string token)
    {
        try
        {
            var result = await _unsubscribeService.ConfirmUnsubscribeAsync(token, "OneClick");
            if (!result.IsValid)
                return BadRequest();

            _logger.LogInformation(
                "RFC 8058 one-click unsubscribe: user {UserId} from {Category}",
                result.UserId, result.Category);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process RFC 8058 one-click unsubscribe");
            return StatusCode(500);
        }
    }
}
