using System.Text.Json;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Options;
using NodaTime;
using Stripe;
using Stripe.Checkout;

namespace Humans.BoxOffice.Services;

internal sealed class BoxOfficeSettings
{
    /// <summary>
    /// STRIPE_BOXOFFICE_KEY: a restricted key with Checkout Sessions write, PaymentIntents and Charges read, Balance
    /// transactions read and Refunds write. Refunds are the one money-out call, capped by Tickets at what the
    /// order's tickets cost and by this adapter at what the charge still holds.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    public string WebhookSecret { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrEmpty(SecretKey);

    public bool IsWebhookConfigured => !string.IsNullOrEmpty(WebhookSecret);
}

/// <summary>
/// The backend order id is the Stripe Checkout Session id. Payment methods are whatever the Stripe account enables
/// for Checkout (card, Klarna, iDEAL); Humans never lists them, so turning one on is a dashboard change.
/// </summary>
internal sealed class BoxOfficeCheckoutBackend(
    IOptions<BoxOfficeSettings> settings,
    IClock clock,
    ILogger<BoxOfficeCheckoutBackend> logger,
    IHttpClient? httpClient = null) : ITicketCheckoutBackend
{
    public const string BackendKey = "boxoffice";
    internal const string OrderMetadataKey = "humans_ticket_order_id";
    internal const string RefundMetadataKey = "humans_ticket_refund_id";

    /// <summary>Stripe's bounds on a Checkout Session's <c>expires_at</c>, with a margin for clock skew.</summary>
    private static readonly Duration MinLifetime = Duration.FromMinutes(31);
    private static readonly Duration MaxLifetime = Duration.FromHours(23);

    /// <summary>Checkout locales Stripe has for Humans' cultures; Catalan falls back to Stripe's own choice.</summary>
    private static readonly HashSet<string> StripeLocales = new(StringComparer.OrdinalIgnoreCase) { "en", "es", "de", "it", "fr", "nl" };

    private readonly BoxOfficeSettings _settings = settings.Value;

    public string Key => BackendKey;

    public bool IsConfigured => _settings.IsConfigured;

    internal bool IsWebhookConfigured => _settings.IsWebhookConfigured;

    private StripeClient Client() => _settings.IsConfigured
        ? new StripeClient(_settings.SecretKey, httpClient: httpClient)
        : throw new TicketCheckoutException("Box Office is not configured: STRIPE_BOXOFFICE_KEY is unset.");

    public async Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct = default)
    {
        if (!string.Equals(request.Currency, "EUR", StringComparison.OrdinalIgnoreCase))
            throw new TicketCheckoutException($"Box Office charges EUR only, not {request.Currency}.");
        if (request.Lines.Any(l => l.AmountMinor < 0) || request.Lines.Sum(l => l.AmountMinor) <= 0)
            throw new TicketCheckoutException("Box Office needs a positive total and no negative lines.");

        var now = clock.GetCurrentInstant();
        var expiresAt = request.ExpiresAt;
        if (expiresAt < now + MinLifetime) expiresAt = now + MinLifetime;
        if (expiresAt > now + MaxLifetime) expiresAt = now + MaxLifetime;

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal) { [OrderMetadataKey] = request.OrderId.ToString() };
        var language = request.Culture.Split('-')[0];
        var options = new SessionCreateOptions
        {
            Mode = "payment",
            SuccessUrl = request.ReturnUrl.ToString(),
            CancelUrl = request.ReturnUrl.ToString(),
            CustomerEmail = request.BuyerEmail,
            ClientReferenceId = request.OrderId.ToString(),
            Locale = StripeLocales.Contains(language) ? language.ToLowerInvariant() : "auto",
            ExpiresAt = expiresAt.ToDateTimeUtc(),
            // Stripe has no zero-amount line; a free line changes nothing in what is charged.
            LineItems = request.Lines.Where(l => l.AmountMinor > 0).Select(l => new SessionLineItemOptions
            {
                Quantity = 1,
                PriceData = new SessionLineItemPriceDataOptions
                {
                    Currency = "eur",
                    UnitAmount = l.AmountMinor,
                    ProductData = new SessionLineItemPriceDataProductDataOptions
                    {
                        Name = l.AttendeeName is { } name ? $"{l.Description} ({name})" : l.Description,
                    },
                },
            }).ToList(),
            Metadata = metadata,
            PaymentIntentData = new SessionPaymentIntentDataOptions
            {
                Description = $"Humans ticket order {request.OrderId}",
                Metadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal),
            },
        };

        try
        {
            var session = await new SessionService(Client()).CreateAsync(
                options, new RequestOptions { IdempotencyKey = $"humans_ticket_checkout_{request.OrderId}" }, ct);
            if (session.Url is null)
                throw new TicketCheckoutException($"Stripe created session {session.Id} without a payment URL.");
            return new CheckoutSession(session.Id, new Uri(session.Url));
        }
        catch (StripeException ex)
        {
            logger.LogWarning("Stripe refused checkout for order {OrderId}: {Message}", request.OrderId, ex.Message);
            throw new TicketCheckoutException($"Stripe refused the checkout: {ex.Message}", ex);
        }
    }

    public async Task<CheckoutStatus> GetStatusAsync(string backendOrderId, CancellationToken ct = default)
    {
        var session = await GetSessionAsync(backendOrderId, ct);
        var intent = session.PaymentIntent;
        var charge = intent?.LatestCharge;

        var state = (session.Status, session.PaymentStatus) switch
        {
            ("complete", "paid" or "no_payment_required") => CheckoutState.Paid,
            ("expired", _) => CheckoutState.Expired,
            // An async method (a bank debit) failed after the buyer completed the page.
            ("complete", _) when intent?.Status is "canceled" or "requires_payment_method" => CheckoutState.Failed,
            _ => CheckoutState.Pending,
        };

        Instant? paidAt = charge is { Paid: true }
            ? Instant.FromDateTimeUtc(DateTime.SpecifyKind(charge.Created, DateTimeKind.Utc))
            : null;
        return new CheckoutStatus(
            state,
            charge?.BalanceTransaction?.Fee,
            charge?.PaymentMethodDetails?.Type,
            paidAt,
            charge?.AmountRefunded ?? 0);
    }

    public async Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        if (request.AmountMinor <= 0)
            return new RefundResult(RefundState.Failed, null, "The refund amount must be positive.");

        var session = await GetSessionAsync(request.BackendOrderId, ct);
        var charge = session.PaymentIntent?.LatestCharge;
        if (charge is not { Paid: true })
            return new RefundResult(RefundState.Failed, null, "The checkout has no settled payment to refund.");
        // Second guard behind Tickets' own: never more than this one payment still holds.
        var remaining = charge.Amount - charge.AmountRefunded;
        if (request.AmountMinor > remaining)
            return new RefundResult(RefundState.Failed, null,
                $"Only {remaining} cents of this payment are left to refund.");

        try
        {
            var refund = await new RefundService(Client()).CreateAsync(
                new RefundCreateOptions
                {
                    PaymentIntent = session.PaymentIntentId,
                    Amount = request.AmountMinor,
                    Reason = "requested_by_customer",
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [RefundMetadataKey] = request.RefundId.ToString(),
                        [OrderMetadataKey] = session.ClientReferenceId ?? string.Empty,
                    },
                },
                new RequestOptions { IdempotencyKey = $"humans_ticket_refund_{request.RefundId}" }, ct);

            var state = refund.Status switch
            {
                "succeeded" => RefundState.Succeeded,
                "failed" or "canceled" => RefundState.Failed,
                _ => RefundState.Pending,
            };
            return new RefundResult(state, refund.Id, state == RefundState.Failed ? refund.FailureReason : null);
        }
        catch (StripeException ex)
        {
            logger.LogWarning("Stripe refused refund {RefundId}: {Message}", request.RefundId, ex.Message);
            return new RefundResult(RefundState.Failed, null, ex.StripeError?.Message ?? ex.Message);
        }
    }

    public async Task CancelAsync(string backendOrderId, CancellationToken ct = default)
    {
        var sessions = new SessionService(Client());
        try
        {
            await sessions.ExpireAsync(backendOrderId, cancellationToken: ct);
        }
        catch (StripeException)
        {
            // Stripe refuses to expire a session that is no longer open; only a paid one is a problem.
            var session = await sessions.GetAsync(backendOrderId, cancellationToken: ct);
            if (string.Equals(session.Status, "complete", StringComparison.Ordinal))
                throw new TicketCheckoutException($"Checkout {backendOrderId} is already complete.");
        }
    }

    /// <summary>
    /// Stripe keeps checkout and payment records as the payment processor under its own legal obligations, and offers
    /// no way to strip them from a session; Humans holds nothing else here.
    /// </summary>
    public Task ErasePersonalDataAsync(string backendOrderId, string email, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>
    /// The session ids a verified webhook is about: the session itself for <c>checkout.session.*</c>, and for a
    /// refund the sessions of the refunded payment. Empty for an invalid signature or an unrelated event.
    /// </summary>
    internal async Task<IReadOnlyList<string>> ParseWebhookAsync(string body, string signature, CancellationToken ct)
    {
        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(body, signature, _settings.WebhookSecret, throwOnApiVersionMismatch: false);
        }
        catch (Exception ex) when (ex is StripeException or JsonException or ArgumentException or InvalidOperationException
            or FormatException or OverflowException)
        {
            logger.LogWarning("Invalid Box Office webhook ({ExceptionType})", ex.GetType().Name);
            return [];
        }

        switch (stripeEvent.Data?.Object)
        {
            case Session session:
                return [session.Id];
            case Charge { PaymentIntentId: { } paymentIntentId }:
                var sessions = await new SessionService(Client()).ListAsync(
                    new SessionListOptions { PaymentIntent = paymentIntentId, Limit = 10 }, cancellationToken: ct);
                return sessions.Data.Select(s => s.Id).ToList();
            default:
                return [];
        }
    }

    private async Task<Session> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            return await new SessionService(Client()).GetAsync(sessionId,
                new SessionGetOptions { Expand = ["payment_intent.latest_charge.balance_transaction"] }, cancellationToken: ct);
        }
        catch (StripeException ex)
        {
            throw new TicketCheckoutException($"Stripe could not read checkout {sessionId}: {ex.Message}", ex);
        }
    }
}
