using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    ITicketCheckoutEvents checkoutEvents,
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

        await checkoutEvents.CheckoutChangedAsync(
            HiEventsCheckoutBackend.BackendKey,
            HiEventsCheckoutBackend.ToBackendOrderId(order.EventId, order.OrderId),
            ct);

        return Ok();
    }
}

internal static class HiEventsWebhookSignature
{
    public static bool IsValid(string body, string signature, string secret)
    {
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant()));
    }

    /// <summary>The event and order an <c>order.*</c> event is about; null for any other event or an unreadable body.</summary>
    public static (int EventId, int OrderId)? ReadOrder(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!root.TryGetProperty("event_type", out var type)
                || type.GetString()?.StartsWith("order.", StringComparison.Ordinal) != true
                || !root.TryGetProperty("payload", out var payload)
                || !payload.TryGetProperty("id", out var id)
                || !payload.TryGetProperty("event_id", out var eventId))
            {
                return null;
            }

            return (eventId.GetInt32(), id.GetInt32());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
