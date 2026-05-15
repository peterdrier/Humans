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

        await _storeService.HandleStripeCheckoutWebhookEventAsync(parsed, ct);

        return Ok();
    }

}


