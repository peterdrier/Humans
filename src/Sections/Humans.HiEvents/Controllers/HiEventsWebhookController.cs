using Humans.HiEvents.Services;
using Humans.Tickets.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.HiEvents.Controllers;

/// <summary>
/// Anonymous: authenticated by the <c>Signature</c> header, Hi.Events' HMAC-SHA256 of the body with the webhook
/// secret. 503 while <c>HIEVENTS_WEBHOOK_SECRET</c> is unset. Only <c>order.*</c> events nudge Tickets, which then
/// reads the order back, so a replayed event does no harm.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("HiEvents/Webhook")]
internal sealed class HiEventsWebhookController(
    IOptions<HiEventsSettings> settings,
    ITicketingPlatformEvents platformEvents,
    ILogger<HiEventsWebhookController> logger) : ControllerBase
{
    [HttpPost("")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        var secret = settings.Value.WebhookSecret;
        if (string.IsNullOrEmpty(secret))
        {
            logger.LogWarning("Hi.Events webhook hit while HIEVENTS_WEBHOOK_SECRET is unset; rejecting.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        string body;
        using (var reader = new StreamReader(Request.Body))
        {
            body = await reader.ReadToEndAsync(ct);
        }

        if (!HiEventsWebhookSignature.IsValid(body, Request.Headers["Signature"].ToString(), secret))
        {
            return Unauthorized();
        }

        var orderId = HiEventsWebhookSignature.ReadOrder(body);
        if (orderId is not { } order)
        {
            return Ok();
        }

        // Applying an order change can write to the platform; a sender that hangs up must not tear it.
        await platformEvents.PlatformOrderChangedAsync(
            HiEventsPlatform.PlatformKey,
            HiEventsPlatform.ToPlatformOrderId(order.EventId, order.OrderId),
            CancellationToken.None);

        return Ok();
    }
}
