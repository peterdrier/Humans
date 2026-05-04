using Humans.Application.Interfaces.Store;
using Humans.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace Humans.Web.Controllers;

/// <summary>
/// Stripe Checkout webhook ingestion for the Store section. Anonymous endpoint;
/// authentication is by signature verification against <c>STRIPE_STORE_WEBHOOK_SECRET</c>.
/// Handles <c>checkout.session.completed</c>; the other three <c>checkout.session.*</c>
/// events (async_payment_succeeded / async_payment_failed / expired) are accepted with a
/// 200 + Warning log until the async-payment state machine is built (see follow-up issue).
/// Unrelated event types log at Debug. Idempotency is enforced downstream by
/// <see cref="IStoreService.RecordStripePaymentAsync"/>.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("Store/StripeWebhook")]
public class StoreStripeWebhookController : ControllerBase
{
    private readonly IStoreService _storeService;
    private readonly StripeSettings _settings;
    private readonly ILogger<StoreStripeWebhookController> _logger;

    public StoreStripeWebhookController(
        IStoreService storeService,
        IOptions<StripeSettings> settings,
        ILogger<StoreStripeWebhookController> logger)
    {
        _storeService = storeService;
        _settings = settings.Value;
        _logger = logger;
    }

    [HttpPost("")]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        if (!_settings.IsStoreWebhookConfigured)
        {
            _logger.LogWarning("Store Stripe webhook hit while STRIPE_STORE_WEBHOOK_SECRET is unset; rejecting.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        string body;
        using (var reader = new StreamReader(Request.Body))
        {
            body = await reader.ReadToEndAsync(ct);
        }

        var signature = Request.Headers["Stripe-Signature"].ToString();

        Event stripeEvent;
        try
        {
            // throwOnApiVersionMismatch=false: webhook handlers parse only the fields they care about,
            // and Stripe maintains backwards compatibility on the relevant event payload shapes.
            stripeEvent = EventUtility.ConstructEvent(
                body, signature, _settings.StoreWebhookSecret,
                throwOnApiVersionMismatch: false);
        }
        catch (StripeException ex)
        {
            _logger.LogWarning("Invalid Stripe webhook signature: {Message}", ex.Message);
            return BadRequest();
        }

        if (string.Equals(stripeEvent.Type, EventTypes.CheckoutSessionCompleted, StringComparison.Ordinal))
        {
            await HandleCheckoutSessionCompletedAsync(stripeEvent, ct);
        }
        else if (string.Equals(stripeEvent.Type, EventTypes.CheckoutSessionAsyncPaymentSucceeded, StringComparison.Ordinal) ||
                 string.Equals(stripeEvent.Type, EventTypes.CheckoutSessionAsyncPaymentFailed, StringComparison.Ordinal) ||
                 string.Equals(stripeEvent.Type, EventTypes.CheckoutSessionExpired, StringComparison.Ordinal))
        {
            // Subscribed to but not yet handled — async-payment state machine pending
            // (nobodies-collective/Humans#638). Surface at Warning so prod ops notice
            // if/when SEPA/Bizum activity starts arriving before the handler ships.
            _logger.LogWarning(
                "Stripe webhook event {Type} (id={EventId}) received but not yet handled — async-payment state machine pending (nobodies-collective/Humans#638).",
                stripeEvent.Type, stripeEvent.Id);
        }
        else
        {
            _logger.LogDebug("Ignoring Stripe webhook event type {Type}", stripeEvent.Type);
        }

        return Ok();
    }

    private async Task HandleCheckoutSessionCompletedAsync(Event stripeEvent, CancellationToken ct)
    {
        if (stripeEvent.Data.Object is not Session session)
        {
            _logger.LogWarning("checkout.session.completed event {Id} did not contain a Session payload", stripeEvent.Id);
            return;
        }

        if (session.Metadata is null ||
            !session.Metadata.TryGetValue("humans_store_order_id", out var orderIdStr) ||
            !Guid.TryParse(orderIdStr, out var orderId))
        {
            _logger.LogWarning(
                "Stripe Checkout Session {SessionId} has no humans_store_order_id metadata; skipping.",
                session.Id);
            return;
        }

        if (string.IsNullOrEmpty(session.PaymentIntentId))
        {
            _logger.LogWarning(
                "Stripe Checkout Session {SessionId} has no PaymentIntentId; skipping.",
                session.Id);
            return;
        }

        // AmountTotal is in minor units (cents); convert back to EUR.
        var amountEur = (session.AmountTotal ?? 0) / 100m;
        if (amountEur <= 0)
        {
            _logger.LogWarning(
                "Stripe Checkout Session {SessionId} has non-positive AmountTotal {Amount}; skipping.",
                session.Id, session.AmountTotal);
            return;
        }

        try
        {
            await _storeService.RecordStripePaymentAsync(orderId, session.PaymentIntentId, amountEur, ct);
            _logger.LogInformation(
                "Recorded Stripe payment for order {OrderId} (session {SessionId}, PI {PaymentIntentId}, EUR {Amount})",
                orderId, session.Id, session.PaymentIntentId, amountEur);
        }
        catch (Exception ex)
        {
            // Surface but don't 500 — Stripe retries on 5xx, and a misbehaving service shouldn't
            // cause endless retry storms. The dedup guard in RecordStripePaymentAsync handles double-deliveries.
            _logger.LogError(ex,
                "Failed to record Stripe payment for order {OrderId} (session {SessionId})",
                orderId, session.Id);
        }
    }
}
