using Humans.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace Humans.Infrastructure.Services;

public class StripeSettings
{
    // Convention: one key per Stripe account. Production keys must be Restricted API Keys (rk_*) with
    // the minimum scopes the integration uses; refunds/payouts/chargebacks stay 100% dashboard-manual.

    /// <summary>Tickets-account key. Populated from STRIPE_TICKETS_KEY. Scopes used: PaymentIntent + BalanceTransaction reads for fee enrichment.</summary>
    public string TicketsKey { get; set; } = string.Empty;

    /// <summary>Store-account key. Populated from STRIPE_STORE_KEY. Scopes used: checkout_session:write (and incidental reads needed by Stripe.NET).</summary>
    public string StoreKey { get; set; } = string.Empty;

    /// <summary>Store-account webhook signing secret (whsec_*). Populated from STRIPE_STORE_WEBHOOK_SECRET.</summary>
    public string StoreWebhookSecret { get; set; } = string.Empty;

    /// <summary>Store-account key with <c>webhook_endpoint:read/write</c> scope. Populated from STRIPE_STORE_WEBHOOK_REGISTRAR_KEY. Set ONLY in ephemeral environments (PR previews) where webhooks self-register at boot — never in QA or production. Kept separate from <see cref="StoreKey"/> so PR-preview testing exercises the production-scoped checkout path with the same scope production has.</summary>
    public string WebhookRegistrarKey { get; set; } = string.Empty;

    /// <summary>True when the Tickets-account key is set (fee enrichment available).</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(TicketsKey);

    /// <summary>True when the Store-account key is set (Checkout Session creation available).</summary>
    public bool IsStoreCheckoutConfigured => !string.IsNullOrEmpty(StoreKey);

    /// <summary>True when the Store webhook signing secret is set (webhook signature verification available).</summary>
    public bool IsStoreWebhookConfigured => !string.IsNullOrEmpty(StoreWebhookSecret);

    /// <summary>True when the dedicated webhook-registrar key is set (auto-registration available — ephemeral envs only).</summary>
    public bool IsWebhookRegistrarConfigured => !string.IsNullOrEmpty(WebhookRegistrarKey);

    /// <summary>GitHub owner (org/user) the webhook registrar queries for the open-PR list when sweeping stale endpoints. Populated from Stripe:WebhookCleanupOwner. Set only when STRIPE_STORE_WEBHOOK_REGISTRAR_KEY is set.</summary>
    public string WebhookCleanupGitHubOwner { get; set; } = string.Empty;

    /// <summary>GitHub repository the webhook registrar queries for the open-PR list. Populated from Stripe:WebhookCleanupRepository.</summary>
    public string WebhookCleanupGitHubRepository { get; set; } = string.Empty;

    /// <summary>True when both the cleanup owner and repository are configured (cross-PR sweep available).</summary>
    public bool IsWebhookCleanupConfigured =>
        !string.IsNullOrEmpty(WebhookCleanupGitHubOwner) && !string.IsNullOrEmpty(WebhookCleanupGitHubRepository);

}

public class StripeService : IStripeService
{
    private readonly StripeSettings _settings;
    private readonly ILogger<StripeService> _logger;

    public StripeService(IOptions<StripeSettings> settings, ILogger<StripeService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public bool IsConfigured => _settings.IsConfigured;

    public bool IsStoreCheckoutConfigured => _settings.IsStoreCheckoutConfigured;

    public bool IsStoreWebhookConfigured => _settings.IsStoreWebhookConfigured;

    /// <summary>
    /// EUR (decimal) → Stripe minor units (long, cents). Half-cent rounds away from zero.
    /// </summary>
    public static long ToStripeMinorUnits(decimal amountEur) =>
        (long)Math.Round(amountEur * 100m, MidpointRounding.AwayFromZero);

    /// <summary>Stripe minor units (cents) → EUR (decimal). Inverse of <see cref="ToStripeMinorUnits"/>.</summary>
    public static decimal FromStripeMinorUnits(long cents) => cents / 100m;

    public async Task<string> CreateCheckoutSessionAsync(
        Guid storeOrderId,
        decimal amountEur,
        string successUrl,
        string cancelUrl,
        string? customerEmail,
        string lineItemDescription,
        CancellationToken ct = default)
    {
        if (amountEur <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountEur), "Checkout amount must be positive.");
        if (!_settings.IsStoreCheckoutConfigured)
            throw new InvalidOperationException("Stripe Store key is not configured.");

        var client = new StripeClient(_settings.StoreKey);
        var sessions = new SessionService(client);

        var options = new SessionCreateOptions
        {
            Mode = "payment",
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            CustomerEmail = customerEmail,
            LineItems =
            [
                new SessionLineItemOptions
                {
                    Quantity = 1,
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = "eur",
                        UnitAmount = ToStripeMinorUnits(amountEur),
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = lineItemDescription,
                        },
                    },
                },
            ],
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["humans_store_order_id"] = storeOrderId.ToString(),
            },
        };

        try
        {
            var session = await sessions.CreateAsync(options, cancellationToken: ct);
            return session.Url;
        }
        catch (StripeException ex) when (StripeStartupSmokeService.IsPermissionError(ex))
        {
            _logger.LogWarning(
                "Stripe permission_error creating Checkout Session for order {OrderId}: the Store key is missing checkout_session:write scope. {Message}",
                storeOrderId, ex.Message);
            throw;
        }
    }

    public async Task<StripePaymentDetails?> GetPaymentDetailsAsync(
        string paymentIntentId, CancellationToken ct = default)
    {
        var client = new StripeClient(_settings.TicketsKey);
        var piService = new PaymentIntentService(client);

        PaymentIntent pi;
        try
        {
            pi = await piService.GetAsync(paymentIntentId, new PaymentIntentGetOptions
            {
                Expand = ["latest_charge.balance_transaction"]
            }, cancellationToken: ct);
        }
        catch (StripeException ex) when (StripeStartupSmokeService.IsPermissionError(ex))
        {
            _logger.LogWarning(
                "Stripe permission_error fetching PaymentIntent {Id}: the Tickets key is missing required scope. {Message}",
                paymentIntentId, ex.Message);
            return null;
        }

        var charge = pi.LatestCharge;
        if (charge is null)
        {
            _logger.LogDebug("PaymentIntent {Id} has no charge", paymentIntentId);
            return null;
        }

        // Payment method type and detail
        var pmd = charge.PaymentMethodDetails;
        var methodType = pmd?.Type ?? "unknown";
        string? methodDetail = null;
        if (string.Equals(methodType, "card", StringComparison.Ordinal) && pmd?.Card is not null)
            methodDetail = pmd.Card.Brand;

        // Fee breakdown from BalanceTransaction
        decimal stripeFee = 0;
        decimal applicationFee = 0;
        var bt = charge.BalanceTransaction;
        if (bt?.FeeDetails is not null)
        {
            foreach (var fd in bt.FeeDetails)
            {
                if (string.Equals(fd.Type, "stripe_fee", StringComparison.Ordinal))
                    stripeFee += FromStripeMinorUnits(fd.Amount);
                else if (string.Equals(fd.Type, "application_fee", StringComparison.Ordinal))
                    applicationFee += FromStripeMinorUnits(fd.Amount);
            }
        }

        return new StripePaymentDetails(
            PaymentMethod: methodType,
            PaymentMethodDetail: methodDetail,
            StripeFee: stripeFee,
            ApplicationFee: applicationFee);
    }

    public StoreCheckoutWebhookEvent? ParseStoreCheckoutEvent(string body, string signature)
    {
        if (!_settings.IsStoreWebhookConfigured)
        {
            _logger.LogWarning("Store webhook parse attempted while STRIPE_STORE_WEBHOOK_SECRET is unset.");
            return null;
        }

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
            return null;
        }

        var kind = stripeEvent.Type switch
        {
            EventTypes.CheckoutSessionCompleted => StoreCheckoutEventKind.CheckoutSessionCompleted,
            EventTypes.CheckoutSessionAsyncPaymentSucceeded => StoreCheckoutEventKind.CheckoutSessionAsyncPaymentSucceeded,
            EventTypes.CheckoutSessionAsyncPaymentFailed => StoreCheckoutEventKind.CheckoutSessionAsyncPaymentFailed,
            EventTypes.CheckoutSessionExpired => StoreCheckoutEventKind.CheckoutSessionExpired,
            _ => StoreCheckoutEventKind.Other,
        };

        StoreCheckoutSessionData? session = null;
        if (stripeEvent.Data.Object is Session s)
        {
            Guid? orderId = null;
            if (s.Metadata is not null &&
                s.Metadata.TryGetValue("humans_store_order_id", out var orderIdStr) &&
                Guid.TryParse(orderIdStr, out var parsed))
            {
                orderId = parsed;
            }

            decimal? amountEur = s.AmountTotal.HasValue ? FromStripeMinorUnits(s.AmountTotal.Value) : null;

            session = new StoreCheckoutSessionData(
                SessionId: s.Id,
                OrderId: orderId,
                PaymentIntentId: string.IsNullOrEmpty(s.PaymentIntentId) ? null : s.PaymentIntentId,
                AmountEur: amountEur);
        }

        return new StoreCheckoutWebhookEvent(stripeEvent.Id, kind, session);
    }
}
