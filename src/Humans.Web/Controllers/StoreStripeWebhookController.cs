using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>
/// Stripe Checkout webhook for Store. Anonymous; auth via signature in IStripeService.ParseStoreCheckoutEvent.
/// Handles checkout.session.completed; idempotency enforced downstream.
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
            return BadRequest();
        }

        await _storeService.HandleStripeCheckoutWebhookEventAsync(parsed, ct);

        return Ok();
    }

}


