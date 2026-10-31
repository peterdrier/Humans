using System.Text.Json;
using Humans.Base.Extensions;
using Humans.Base.Helpers;
using Humans.Stripe.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
// Inside `Humans.Stripe.Services` the bare name `Stripe` binds to this section. Never write `Stripe.Xxx`
// inline here: use the simple name or `global::Stripe.Xxx`.
using Stripe;
using Stripe.Checkout;

namespace Humans.Stripe.Services;

internal sealed class StripeSettings
{
    /// <summary>STRIPE_TICKETS_KEY: PaymentIntent and BalanceTransaction reads.</summary>
    public string TicketsKey { get; set; } = string.Empty;

    /// <summary>STRIPE_STORE_KEY: <c>checkout_session:write</c>, which includes read, so it can also list sessions.</summary>
    public string StoreKey { get; set; } = string.Empty;

    public string StoreWebhookSecret { get; set; } = string.Empty;

    /// <summary>STRIPE_STORE_WEBHOOK_REGISTRAR_KEY (<c>webhook_endpoint:read/write</c>). PR previews only; never QA or production.</summary>
    public string WebhookRegistrarKey { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrEmpty(TicketsKey);

    public bool IsStoreCheckoutConfigured => !string.IsNullOrEmpty(StoreKey);

    public bool IsStoreWebhookConfigured => !string.IsNullOrEmpty(StoreWebhookSecret);

    public bool IsWebhookRegistrarConfigured => !string.IsNullOrEmpty(WebhookRegistrarKey);

    /// <summary>GitHub owner whose open-PR list the registrar's sweep reads.</summary>
    public string WebhookCleanupGitHubOwner { get; set; } = string.Empty;

    public string WebhookCleanupGitHubRepository { get; set; } = string.Empty;

    public bool IsWebhookCleanupConfigured =>
        !string.IsNullOrEmpty(WebhookCleanupGitHubOwner) && !string.IsNullOrEmpty(WebhookCleanupGitHubRepository);

}

internal sealed class StripeService(
    IOptions<StripeSettings> settings, ILogger<StripeService> logger, IHttpClient? httpClient = null) : IStripeService
{
    private readonly StripeSettings _settings = settings.Value;

    public bool IsConfigured => _settings.IsConfigured;

    public bool IsStoreCheckoutConfigured => _settings.IsStoreCheckoutConfigured;

    public bool IsStoreWebhookConfigured => _settings.IsStoreWebhookConfigured;

    /// <summary>Half a cent rounds away from zero.</summary>
    public static long ToStripeMinorUnits(decimal amountEur) =>
        (long)Math.Round(amountEur * 100m, MidpointRounding.AwayFromZero);

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
        using var _ = logger.TimeOperation();
        if (amountEur <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountEur), "Checkout amount must be positive.");
        if (!_settings.IsStoreCheckoutConfigured)
            throw new InvalidOperationException("Stripe Store key is not configured.");

        var client = new StripeClient(_settings.StoreKey, httpClient: httpClient);
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
            // Stamp the order id on the PaymentIntent too, so the dashboard, receipt and PI search can match it.
            PaymentIntentData = new SessionPaymentIntentDataOptions
            {
                Description = lineItemDescription,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["humans_store_order_id"] = storeOrderId.ToString(),
                },
            },
        };

        try
        {
            var session = await sessions.CreateAsync(options, cancellationToken: ct);
            return session.Url;
        }
        catch (StripeException ex) when (StripeStartupSmokeService.IsPermissionError(ex))
        {
            logger.LogWarning(
                "Stripe permission_error creating Checkout Session for order {OrderId}: the Store key is missing checkout_session:write scope. {Message}",
                storeOrderId, ex.Message);
            throw;
        }
    }

    public async Task<StripePaymentDetails?> GetPaymentDetailsAsync(
        string paymentIntentId, CancellationToken ct = default)
    {
        using var _ = logger.TimeOperation();
        if (!_settings.IsConfigured)
        {
            logger.LogWarning("PaymentIntent {Id} requested while STRIPE_TICKETS_KEY is unset; Stripe not queried.", paymentIntentId);
            return null;
        }

        var client = new StripeClient(_settings.TicketsKey, httpClient: httpClient);
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
            logger.LogWarning(
                "Stripe permission_error fetching PaymentIntent {Id}: the Tickets key is missing required scope. {Message}",
                paymentIntentId, ex.Message);
            return null;
        }

        var charge = pi.LatestCharge;
        if (charge is null)
        {
            logger.LogDebug("PaymentIntent {Id} has no charge", paymentIntentId);
            return null;
        }

        // Asynchronous capture can succeed before its balance transaction exists. Returning
        // zero here would permanently mark Tickets' fee enrichment complete instead of retrying.
        var bt = charge.BalanceTransaction;
        if (bt is null)
        {
            logger.LogDebug("PaymentIntent {Id} is awaiting its balance transaction", paymentIntentId);
            return null;
        }
        if (!string.Equals(bt.Currency, "eur", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Stripe balance transaction does not declare EUR currency.");

        var pmd = charge.PaymentMethodDetails;
        var methodType = pmd?.Type ?? "unknown";
        string? methodDetail = null;
        if (string.Equals(methodType, "card", StringComparison.Ordinal) && pmd?.Card is not null)
            methodDetail = pmd.Card.Brand;

        decimal stripeFee = 0;
        decimal applicationFee = 0;
        decimal feeTax = 0;
        decimal withheldTax = 0;
        if (bt.FeeDetails is not null)
        {
            foreach (var fd in bt.FeeDetails)
            {
                if (!string.Equals(fd.Currency, "eur", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Stripe fee does not declare EUR currency.");
                var amount = FromStripeMinorUnits(fd.Amount);
                switch (fd.Type)
                {
                    case "stripe_fee" or "payment_method_passthrough_fee":
                        stripeFee += amount;
                        break;
                    case "application_fee":
                        applicationFee += amount;
                        break;
                    case "tax":
                        feeTax += amount;
                        break;
                    case "withheld_tax":
                        withheldTax += amount;
                        break;
                }
            }
        }

        // An unknown future fee type would otherwise vanish from the books silently.
        var captured = stripeFee + applicationFee + feeTax + withheldTax;
        var total = FromStripeMinorUnits(bt.Fee);
        if (captured != total)
            logger.LogWarning(
                "PaymentIntent {Id}: fee buckets sum to {Captured} but the balance transaction fee is {Total}; an unrecognised fee type is unaccounted for.",
                paymentIntentId, captured, total);

        return new StripePaymentDetails(
            PaymentMethod: methodType,
            PaymentMethodDetail: methodDetail,
            StripeFee: stripeFee,
            ApplicationFee: applicationFee,
            FeeTax: feeTax,
            WithheldTax: withheldTax);
    }

    public StoreCheckoutWebhookEvent? ParseStoreCheckoutEvent(string body, string signature)
    {
        using var _ = logger.TimeOperation();
        if (!_settings.IsStoreWebhookConfigured)
        {
            logger.LogWarning("Store webhook parse attempted while STRIPE_STORE_WEBHOOK_SECRET is unset.");
            return null;
        }

        Event stripeEvent;
        try
        {
            // Webhook handlers parse only the fields they care about, and Stripe maintains
            // backwards compatibility on the relevant event payload shapes.
            stripeEvent = EventUtility.ConstructEvent(
                body, signature, _settings.StoreWebhookSecret,
                throwOnApiVersionMismatch: false);
        }
        catch (StripeException ex)
        {
            logger.LogWarning("Invalid Stripe webhook signature: {Message}", ex.Message);
            return null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException
            or FormatException or OverflowException)
        {
            // Signature verification can succeed before the SDK rejects the payload's shape.
            logger.LogWarning("Invalid Stripe webhook payload ({ExceptionType}).", ex.GetType().Name);
            return null;
        }

        if (stripeEvent is null || string.IsNullOrWhiteSpace(stripeEvent.Id)
            || string.IsNullOrWhiteSpace(stripeEvent.Type) || stripeEvent.Data is null)
        {
            logger.LogWarning("Invalid Stripe webhook payload: event identity, type and data are required.");
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
            session = MapSession(s);
            if (session is null) return null;
        }
        else if (kind != StoreCheckoutEventKind.Other)
        {
            logger.LogWarning("Invalid Stripe checkout webhook payload: a Checkout Session is required.");
            return null;
        }

        return new StoreCheckoutWebhookEvent(stripeEvent.Id, kind, session);
    }

    public async Task<IReadOnlyList<StoreCheckoutSessionData>?> ListStoreCheckoutSessionsAsync(
        CancellationToken ct = default)
    {
        using var _ = logger.TimeOperation();
        if (!_settings.IsStoreCheckoutConfigured)
        {
            logger.LogWarning("Store Checkout Session list requested while STRIPE_STORE_KEY is unset; Stripe not queried.");
            return null;
        }

        var client = new StripeClient(_settings.StoreKey, httpClient: httpClient);
        var sessions = new SessionService(client);
        var results = new List<StoreCheckoutSessionData>();
        try
        {
            await foreach (var s in sessions.ListAutoPagingAsync(new SessionListOptions { Limit = 100 }, cancellationToken: ct))
            {
                results.Add(MapSession(s) ?? throw new InvalidOperationException(
                    "Stripe Store Checkout list contains a session without explicit EUR currency."));
            }
        }
        catch (StripeException ex) when (StripeStartupSmokeService.IsPermissionError(ex))
        {
            logger.LogWarning(
                "Stripe permission_error listing Store Checkout Sessions: the Store key is missing checkout_session:read scope. {Message}",
                ex.Message);
            return null;
        }

        return results;
    }

    /// <summary>Shared by the webhook and reconciliation paths so both read a session identically.</summary>
    private StoreCheckoutSessionData? MapSession(Session s)
    {
        if (!string.Equals(s.Currency, "eur", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Rejected Stripe Checkout session {SessionId} with unsupported currency {Currency}; EUR is required",
                IbanFormatter.MaskAllIn(s.Id), IbanFormatter.MaskAllIn(s.Currency ?? "(missing)"));
            return null;
        }

        Guid? orderId = null;
        if (s.Metadata is not null &&
            s.Metadata.TryGetValue("humans_store_order_id", out var orderIdStr) &&
            Guid.TryParse(orderIdStr, out var parsed))
        {
            orderId = parsed;
        }

        return new StoreCheckoutSessionData(
            SessionId: s.Id,
            OrderId: orderId,
            PaymentIntentId: string.IsNullOrEmpty(s.PaymentIntentId) ? null : s.PaymentIntentId,
            AmountEur: s.AmountTotal.HasValue ? FromStripeMinorUnits(s.AmountTotal.Value) : null,
            PaymentStatus: s.PaymentStatus,
            CreatedAt: Instant.FromDateTimeUtc(DateTime.SpecifyKind(s.Created, DateTimeKind.Utc)));
    }
}
