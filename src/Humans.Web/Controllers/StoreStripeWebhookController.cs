using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>
/// Stripe Checkout webhook ingestion for the Store section. Anonymous endpoint;
/// authentication is by signature verification against <c>STRIPE_STORE_WEBHOOK_SECRET</c>,
/// performed inside <see cref="IStripeService.ParseStoreCheckoutEvent"/> so the Web layer
/// never imports Stripe SDK types (design-rules §15i — connector pattern).
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
    private readonly IStripeService _stripeService;
    private readonly ILogger<StoreStripeWebhookController> _logger;

    public StoreStripeWebhookController(
        IStoreService storeService,
        IStripeService stripeService,
        ILogger<StoreStripeWebhookController> logger)
    {
        _storeService = storeService;
        _stripeService = stripeService;
        _logger = logger;
    }

    [HttpPost("")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        if (!_stripeService.IsStoreWebhookConfigured)
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

        var parsed = _stripeService.ParseStoreCheckoutEvent(body, signature);
        if (parsed is null)
        {
            // Service has already logged the reason (signature failure or secret unset).
            return BadRequest();
        }

        switch (parsed.Kind)
        {
            case StoreCheckoutEventKind.CheckoutSessionCompleted:
                await HandleCheckoutSessionCompletedAsync(parsed, ct);
                break;

            case StoreCheckoutEventKind.CheckoutSessionAsyncPaymentSucceeded:
            case StoreCheckoutEventKind.CheckoutSessionAsyncPaymentFailed:
            case StoreCheckoutEventKind.CheckoutSessionExpired:
                // Subscribed to but not yet handled — async-payment state machine pending
                // (nobodies-collective/Humans#638). Surface at Warning so prod ops notice
                // if/when SEPA/Bizum activity starts arriving before the handler ships.
                _logger.LogWarning(
                    "Stripe webhook event {Kind} (id={EventId}) received but not yet handled — async-payment state machine pending (nobodies-collective/Humans#638).",
                    parsed.Kind, parsed.EventId);
                break;

            default:
                _logger.LogDebug("Ignoring Stripe webhook event {EventId} of unhandled kind {Kind}", parsed.EventId, parsed.Kind);
                break;
        }

        return Ok();
    }

    private async Task HandleCheckoutSessionCompletedAsync(StoreCheckoutWebhookEvent evt, CancellationToken ct)
    {
        if (evt.Session is not { } session)
        {
            _logger.LogWarning("checkout.session.completed event {EventId} did not contain a Session payload", evt.EventId);
            return;
        }

        if (session.OrderId is not { } orderId)
        {
            _logger.LogWarning(
                "Stripe Checkout Session {SessionId} has no humans_store_order_id metadata; skipping.",
                session.SessionId);
            return;
        }

        if (session.PaymentIntentId is not { } paymentIntentId)
        {
            _logger.LogWarning(
                "Stripe Checkout Session {SessionId} has no PaymentIntentId; skipping.",
                session.SessionId);
            return;
        }

        if (session.AmountEur is not { } amountEur || amountEur <= 0)
        {
            _logger.LogWarning(
                "Stripe Checkout Session {SessionId} has non-positive AmountTotal; skipping.",
                session.SessionId);
            return;
        }

        try
        {
            await _storeService.RecordStripePaymentAsync(orderId, paymentIntentId, amountEur, ct);
            _logger.LogInformation(
                "Recorded Stripe payment for order {OrderId} (session {SessionId}, PI {PaymentIntentId}, EUR {Amount})",
                orderId, session.SessionId, paymentIntentId, amountEur);
        }
        catch (Exception ex)
        {
            // Surface but don't 500 — Stripe retries on 5xx, and a misbehaving service shouldn't
            // cause endless retry storms. The dedup guard in RecordStripePaymentAsync handles double-deliveries.
            _logger.LogError(ex,
                "Failed to record Stripe payment for order {OrderId} (session {SessionId})",
                orderId, session.SessionId);
        }
    }
}
