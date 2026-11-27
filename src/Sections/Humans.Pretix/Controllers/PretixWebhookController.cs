using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Humans.Pretix.Services;
using Humans.Tickets.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Humans.Pretix.Controllers;

/// <summary>
/// pretix signs nothing, so the URL carries a shared secret (in the query, which request logging leaves out) and the body is only a nudge: Tickets re-reads the order
/// from pretix's API, which is the truth, so a forged or replayed call can at most cause one extra read. 503 while
/// the secret is unset; a failing nudge propagates so pretix retries.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("Pretix/Webhook")]
internal sealed class PretixWebhookController(
    ITicketingPlatformEvents platformEvents,
    IOptions<PretixSettings> options,
    ILogger<PretixWebhookController> logger) : ControllerBase
{
    [HttpPost]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Receive([FromQuery] string? secret, [FromBody] PretixNotification notification, CancellationToken ct)
    {
        var settings = options.Value;
        if (string.IsNullOrEmpty(settings.WebhookSecret))
        {
            logger.LogWarning("pretix webhook hit while PRETIX_WEBHOOK_SECRET is unset; rejecting.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(secret ?? string.Empty), Encoding.UTF8.GetBytes(settings.WebhookSecret)))
            return NotFound();

        // Other organizers on a shared instance, and order-less actions, are not ours.
        if (!string.Equals(notification.Organizer, settings.Organizer, StringComparison.Ordinal)
            || string.IsNullOrEmpty(notification.Event) || string.IsNullOrEmpty(notification.Code))
            return Ok();

        // Applying an order change can write to the platform; a sender that hangs up must not tear it.
        await platformEvents.PlatformOrderChangedAsync(
            PretixPlatform.PlatformKey,
            PretixPlatform.FormatPlatformOrderId(notification.Event, notification.Code),
            CancellationToken.None);
        return Ok();
    }

    internal sealed record PretixNotification(
        [property: JsonPropertyName("organizer")] string? Organizer,
        [property: JsonPropertyName("event")] string? Event,
        [property: JsonPropertyName("code")] string? Code);
}
