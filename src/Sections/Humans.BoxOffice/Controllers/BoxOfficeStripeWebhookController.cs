using Humans.BoxOffice.Services;
using Humans.Tickets.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.BoxOffice.Controllers;

/// <summary>
/// Anonymous: authenticated by the Stripe signature. Only a nudge: Tickets reads the session's state back from Stripe,
/// so a replay changes nothing. 503 while either <c>STRIPE_BOXOFFICE_*</c> variable is unset; 400 on a bad signature, so
/// a wrong secret shows as failed deliveries in Stripe instead of green ones Humans ignores.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("BoxOffice/StripeWebhook")]
internal sealed class BoxOfficeStripeWebhookController(
    BoxOfficePlatform platform,
    ITicketingPlatformEvents platformEvents,
    ILogger<BoxOfficeStripeWebhookController> logger) : ControllerBase
{
    [HttpPost("")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        if (!platform.IsWebhookConfigured || !platform.IsConfigured)
        {
            logger.LogWarning("Box Office webhook hit while STRIPE_BOXOFFICE_* is unset; rejecting.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        string body;
        using (var reader = new StreamReader(Request.Body))
            body = await reader.ReadToEndAsync(ct);

        var sessionIds = await platform.ParseWebhookAsync(body, Request.Headers["Stripe-Signature"].ToString(), ct);
        if (sessionIds is null)
            return BadRequest();
        // Applying an order change can write to the platform; a sender that hangs up must not tear it.
        foreach (var sessionId in sessionIds)
            await platformEvents.PlatformOrderChangedAsync(BoxOfficePlatform.PlatformKey, sessionId, CancellationToken.None);

        return Ok();
    }
}
