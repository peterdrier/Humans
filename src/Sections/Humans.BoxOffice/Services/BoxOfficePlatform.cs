using System.Globalization;
using System.Text.Json;
using Humans.Stripe.Contracts;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Options;
using NodaTime;
using Stripe;
using Stripe.Checkout;

namespace Humans.BoxOffice.Services;

internal sealed class BoxOfficeSettings
{
    /// <summary>
    /// STRIPE_TICKETS_KEY, a restricted key (its scopes are listed in <see cref="BoxOfficePlatform.Setup"/>).
    /// Refunds are the one money-out call, capped by Tickets at what the order's tickets cost and by this adapter at what
    /// the charge still holds.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    public string WebhookSecret { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrEmpty(SecretKey) && !SecretKey.Any(char.IsWhiteSpace);

    public bool IsWebhookConfigured => !string.IsNullOrEmpty(WebhookSecret);
}

/// <summary>
/// The platform order id is the Stripe Checkout Session id. Payment methods are whatever the Stripe account enables
/// for Checkout (card, Klarna, iDEAL); Humans never lists them, so turning one on is a dashboard change.
/// </summary>
internal sealed class BoxOfficePlatform(
    IOptions<BoxOfficeSettings> settings,
    IStripeService stripe,
    IClock clock,
    ILogger<BoxOfficePlatform> logger,
    IHttpClient? httpClient = null) : ITicketingPlatform
{
    public const string PlatformKey = "boxoffice";
    internal const string OrderMetadataKey = "humans_ticket_order_id";
    internal const string RefundMetadataKey = "humans_ticket_refund_id";
    internal const string TicketCountMetadataKey = "humans_ticket_count";

    /// <summary>Stripe's bounds on a Checkout Session's <c>expires_at</c>, with a margin for clock skew.</summary>
    private static readonly Duration MinLifetime = Duration.FromMinutes(31);
    private static readonly Duration MaxLifetime = Duration.FromHours(23);

    /// <summary>Checkout locales Stripe has for Humans' cultures; Catalan falls back to Stripe's own choice.</summary>
    private static readonly HashSet<string> StripeLocales = new(StringComparer.OrdinalIgnoreCase) { "en", "es", "de", "it", "fr", "nl" };

    private readonly BoxOfficeSettings _settings = settings.Value;

    public string Key => PlatformKey;

    public bool IsConfigured => _settings.IsConfigured;

    internal bool IsWebhookConfigured => _settings.IsWebhookConfigured;

    private StripeClient Client() => _settings.IsConfigured
        ? new StripeClient(_settings.SecretKey, httpClient: httpClient)
        : throw new TicketingPlatformException("Box Office is not configured: STRIPE_TICKETS_KEY is unset or contains whitespace.");

    public async Task<PlatformOrderSession> CreateOrderAsync(PlatformOrderRequest request, CancellationToken ct = default)
    {
        if (!string.Equals(request.Currency, "EUR", StringComparison.OrdinalIgnoreCase))
            throw new TicketingPlatformException($"Box Office charges EUR only, not {request.Currency}.");
        if (request.Lines.Any(l => l.AmountMinor < 0) || request.Lines.Sum(l => l.AmountMinor) <= 0)
            throw new TicketingPlatformException("Box Office needs a positive total and no negative lines.");

        var now = clock.GetCurrentInstant();
        var expiresAt = request.ExpiresAt;
        if (expiresAt < now + MinLifetime) expiresAt = now + MinLifetime;
        if (expiresAt > now + MaxLifetime) expiresAt = now + MaxLifetime;

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal) { [OrderMetadataKey] = request.OrderId.ToString() };
        // Free ticket lines never reach Stripe, so reconciliation reads the ticket count from here.
        var sessionMetadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal)
        {
            [TicketCountMetadataKey] = request.Lines.Count(l => l.Kind == OrderLineKind.Ticket).ToString(CultureInfo.InvariantCulture),
        };
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
                        Name = l.HolderName is { } name ? $"{l.Description} ({name})" : l.Description,
                    },
                },
            }).ToList(),
            Metadata = sessionMetadata,
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
                throw new TicketingPlatformException($"Stripe created session {session.Id} without a payment URL.");
            return new PlatformOrderSession(session.Id, new Uri(session.Url));
        }
        catch (StripeException ex)
        {
            logger.LogWarning("Stripe refused the Checkout Session for order {OrderId}: {Message}", request.OrderId, ex.Message);
            throw new TicketingPlatformException($"Stripe refused the Checkout Session: {ex.Message}", ex);
        }
    }

    public async Task<PlatformOrderStatus> GetStatusAsync(string platformOrderId, CancellationToken ct = default) =>
        await StatusOfAsync(await GetSessionAsync(platformOrderId, ct), everyRefund: true, ct);

    /// <summary>
    /// The session's payment intent and latest charge must be expanded. Without <paramref name="everyRefund"/> the
    /// refunds are read only for a charge that shows some, which misses a refund that failed (it drops out of the
    /// charge's total): fine for totals, not for <see cref="PlatformOrderStatus.RefundFailed"/>.
    /// </summary>
    private async Task<PlatformOrderStatus> StatusOfAsync(Session session, bool everyRefund, CancellationToken ct)
    {
        var intent = session.PaymentIntent;
        var charge = intent?.LatestCharge;

        var state = (session.Status, session.PaymentStatus) switch
        {
            ("complete", "paid" or "no_payment_required") => PlatformOrderState.Paid,
            ("expired", _) => PlatformOrderState.Expired,
            // An async method (a bank debit) failed after the buyer completed the page.
            ("complete", _) when IsFailed(intent) => PlatformOrderState.Failed,
            // A debit settling, or a bank transfer the buyer still has to send: the session can no longer be expired.
            ("complete", _) => PlatformOrderState.Processing,
            _ => PlatformOrderState.Pending,
        };

        Instant? paidAt = charge is { Paid: true } ? InstantOf(charge.Created) : null;
        // charge.amount_refunded counts a refund from the moment it is created, pending included, and a failed one
        // drops back out: read the refunds themselves so only money that went back counts.
        var refunds = session.PaymentIntentId is { } paymentIntentId && (everyRefund || charge is { AmountRefunded: > 0 })
            ? await ListRefundsAsync(paymentIntentId, ct)
            : [];
        return new PlatformOrderStatus(
            state,
            charge is not null && session.PaymentIntentId is { } feeIntent
                ? await stripe.GetAccountPaymentFeeAsync("Tickets", feeIntent, ct) : null,
            charge?.PaymentMethodDetails?.Type,
            paidAt,
            refunds.Where(r => StateOf(r) == RefundState.Succeeded).Sum(r => r.Amount),
            refunds.Any(r => StateOf(r) == RefundState.Failed && r.Metadata?.ContainsKey(RefundMetadataKey) == true),
            charge is { Disputed: true, Id: { } chargeId } ? await ChargedBackAsync(chargeId, ct) : 0);
    }

    public string? EventRefOf(string platformOrderId) => null;

    /// <summary>
    /// Lists every Checkout Session on the account, since the Stripe account sells tickets only and a session Humans
    /// did not open is exactly what reconciliation must show. Box Office keeps no events, so the event id is ignored.
    /// </summary>
    public async Task<IReadOnlyList<PlatformOrderSummary>> ListOrdersAsync(
        string? platformEventRef, Instant from, Instant to, CancellationToken ct = default)
    {
        var options = new SessionListOptions
        {
            Created = new DateRangeOptions { GreaterThanOrEqual = from.ToDateTimeUtc(), LessThan = to.ToDateTimeUtc() },
            Limit = 100,
            Expand = ["data.payment_intent.latest_charge"],
        };
        var orders = new List<PlatformOrderSummary>();
        try
        {
            await foreach (var session in new SessionService(Client()).ListAutoPagingAsync(options, cancellationToken: ct))
            {
                var status = await StatusOfAsync(session, everyRefund: false, ct);
                orders.Add(new PlatformOrderSummary(
                    session.Id,
                    Guid.TryParse(session.Metadata?.GetValueOrDefault(OrderMetadataKey), out var orderId) ? orderId : null,
                    InstantOf(session.Created),
                    status.State,
                    session.AmountTotal ?? 0,
                    int.TryParse(session.Metadata?.GetValueOrDefault(TicketCountMetadataKey), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var tickets) ? tickets : null,
                    status.RefundedMinor,
                    status.ChargedBackMinor));
            }
        }
        catch (StripeException ex)
        {
            throw new TicketingPlatformException($"Stripe could not list Checkout Sessions: {ex.Message}", ex);
        }

        return orders;
    }

    public async Task<PaymentLedger?> GetPaymentLedgerAsync(Instant from, Instant to, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return null;
        StripeAccountLedger? account;
        try
        {
            account = await stripe.GetAccountLedgerAsync("Tickets", from, to, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new TicketingPlatformException(ex.Message, ex);
        }
        if (account is null)
            return null;

        return new PaymentLedger(account.Lines.Select(line => new PaymentLedgerEntry(
            line.Id, line.CreatedAt, PaymentLedgerEntry.KindOf(line.ReportingCategory), line.AmountMinor, line.FeeMinor,
            line.Payment is { } payment && payment.Metadata.TryGetValue(OrderMetadataKey, out var value)
                && Guid.TryParse(value, out var id) ? id : null,
            line.Description)).ToList(), account.Payouts.Select(p => new PaymentPayout(
                p.Id, p.CreatedAt, p.ArrivalAt, p.AmountMinor, p.Status, p.ContentsNetMinor, p.ContentsCount)).ToList());
    }

    private static Instant InstantOf(DateTime utc) => Instant.FromDateTimeUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    private async Task<long> ChargedBackAsync(string chargeId, CancellationToken ct)
    {
        try
        {
            return (await new DisputeService(Client()).ListAsync(new DisputeListOptions { Charge = chargeId, Limit = 100 },
                cancellationToken: ct)).Data.Where(TakesMoney).Sum(d => d.Amount);
        }
        catch (StripeException ex)
        {
            throw new TicketingPlatformException($"Stripe could not list the disputes of {chargeId}: {ex.Message}", ex);
        }
    }

    private static bool IsFailed(PaymentIntent? intent) => intent?.Status is "canceled" or "requires_payment_method";

    /// <summary>An inquiry (<c>warning_*</c>) takes nothing; any other dispute holds the money until it is won.</summary>
    private static bool TakesMoney(Dispute dispute) =>
        dispute.Status is not ("won" or "warning_needs_response" or "warning_under_review" or "warning_closed");

    private async Task<IReadOnlyList<Refund>> ListRefundsAsync(string paymentIntentId, CancellationToken ct)
    {
        try
        {
            return (await new RefundService(Client()).ListAsync(
                new RefundListOptions { PaymentIntent = paymentIntentId, Limit = 100 }, cancellationToken: ct)).Data;
        }
        catch (StripeException ex)
        {
            throw new TicketingPlatformException($"Stripe could not list the refunds of {paymentIntentId}: {ex.Message}", ex);
        }
    }

    private static RefundState StateOf(Refund refund) => refund.Status switch
    {
        "succeeded" => RefundState.Succeeded,
        "failed" or "canceled" => RefundState.Failed,
        _ => RefundState.Pending,
    };

    public async Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        if (request.AmountMinor <= 0)
            return new RefundResult(RefundState.Failed, null, "The refund amount must be positive.");

        var session = await GetSessionAsync(request.PlatformOrderId, ct);
        var charge = session.PaymentIntent?.LatestCharge;
        if (charge is not { Paid: true })
            return new RefundResult(RefundState.Failed, null, "The order has no settled payment to refund.");
        // A repeat of a refund whose answer was lost gets that refund's own state, not the cap's refusal.
        var earlier = (await ListRefundsAsync(session.PaymentIntentId!, ct)).FirstOrDefault(r =>
            r.Metadata?.TryGetValue(RefundMetadataKey, out var id) == true
            && string.Equals(id, request.RefundId.ToString(), StringComparison.Ordinal));
        if (earlier is not null)
            return new RefundResult(StateOf(earlier), earlier.Id, StateOf(earlier) == RefundState.Failed ? earlier.FailureReason : null);
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

            var state = StateOf(refund);
            return new RefundResult(state, refund.Id, state == RefundState.Failed ? refund.FailureReason : null);
        }
        catch (StripeException ex) when (ex.StripeError is not null)
        {
            logger.LogWarning("Stripe refused refund {RefundId}: {Message}", request.RefundId, ex.Message);
            return new RefundResult(RefundState.Failed, null, ex.StripeError.Message ?? ex.Message);
        }
        catch (StripeException ex)
        {
            // No answer from Stripe: the refund may exist, so Tickets keeps it pending and the repeat finds it.
            throw new TicketingPlatformException($"Stripe did not answer refund {request.RefundId}: {ex.Message}", ex);
        }
    }

    public async Task CancelAsync(string platformOrderId, CancellationToken ct = default)
    {
        try
        {
            await new SessionService(Client()).ExpireAsync(platformOrderId, cancellationToken: ct);
        }
        catch (StripeException)
        {
            // Stripe refuses to expire a session that is no longer open; only one that is or may still be paid is a problem.
            var session = await GetSessionAsync(platformOrderId, ct);
            if (string.Equals(session.Status, "complete", StringComparison.Ordinal) && !IsFailed(session.PaymentIntent))
                throw new TicketingPlatformException($"Checkout Session {platformOrderId} is already complete.");
        }
    }

    /// <summary>
    /// Stripe keeps Checkout Session and payment records as the payment processor under its own legal obligations, and offers
    /// no way to strip them from a session; Humans holds nothing else here.
    /// </summary>
    public Task ErasePersonalDataAsync(string platformOrderId, string email, CancellationToken ct = default) =>
        Task.CompletedTask;

    public PlatformSetup Setup => new(
        "Box Office (Stripe)",
        $"Stripe, Humans' ticket account ({ModeLabel})",
        new Uri(IsTestKey ? "https://dashboard.stripe.com/test/payments" : "https://dashboard.stripe.com/payments"),
        [
            new(Section.KeyVariable, "Tickets account restricted key: Checkout Sessions write, PaymentIntents, Charges and Disputes read, Refunds write",
                IsSecret: true, _settings.IsConfigured, null),
            new(Section.WebhookSecretVariable, "Signing secret of the webhook endpoint below", IsSecret: true,
                _settings.IsWebhookConfigured, null),
        ],
        "/BoxOffice/StripeWebhook",
        [
            "Webhook events: checkout.session.completed, checkout.session.async_payment_succeeded, checkout.session.async_payment_failed, checkout.session.expired, charge.refunded, refund.updated, refund.failed, charge.dispute.created, charge.dispute.closed, charge.dispute.funds_withdrawn, charge.dispute.funds_reinstated.",
            "Payment methods (card, iDEAL, Klarna) are switched on in the Stripe dashboard; Humans never lists them.",
            "Needs no event id in the ticketing settings.",
        ]);

    private bool IsTestKey => _settings.SecretKey.StartsWith("sk_test_", StringComparison.Ordinal)
        || _settings.SecretKey.StartsWith("rk_test_", StringComparison.Ordinal);

    private string ModeLabel => !_settings.IsConfigured ? "key unset" : IsTestKey ? "test mode" : "live mode";

    public async Task<IReadOnlyList<PlatformFinding>> CheckAsync(string? platformEventRef, CancellationToken ct = default)
    {
        if (!_settings.IsConfigured)
            return [new(false, $"{Section.KeyVariable} is unset or contains whitespace.")];

        var findings = (await stripe.CheckAccountAsync("Tickets", ct, includeCheckout: true))
            .Select(c => new PlatformFinding(c.Ok, c.Text)).ToList();

        findings.Add(_settings.IsWebhookConfigured
            ? new(true, "Webhook signing secret is set.")
            : new(false, $"{Section.WebhookSecretVariable} is unset: the webhook answers 503 and outcomes arrive only from the return page and the 5-minute poll."));
        if (!string.IsNullOrWhiteSpace(platformEventRef))
            findings.Add(new(false, "Box Office takes no event id; clear it in the ticketing settings."));
        return findings;
    }

    /// <summary>
    /// The session ids a verified webhook is about: the session itself for <c>checkout.session.*</c>, and for a
    /// charge or refund the sessions of its payment. Empty for an unrelated event; null for an invalid signature.
    /// </summary>
    internal async Task<IReadOnlyList<string>?> ParseWebhookAsync(string body, string signature, CancellationToken ct)
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
            return null;
        }

        switch (stripeEvent.Data?.Object)
        {
            case Session session:
                return [session.Id];
            case Charge { PaymentIntentId: { } chargeIntent }:
                return await SessionsForAsync(chargeIntent, ct);
            case Refund { PaymentIntentId: { } refundIntent }:
                return await SessionsForAsync(refundIntent, ct);
            case Dispute { PaymentIntentId: { } disputeIntent }:
                return await SessionsForAsync(disputeIntent, ct);
            default:
                return [];
        }
    }

    private async Task<IReadOnlyList<string>> SessionsForAsync(string paymentIntentId, CancellationToken ct) =>
        (await new SessionService(Client()).ListAsync(
            new SessionListOptions { PaymentIntent = paymentIntentId, Limit = 10 }, cancellationToken: ct)).Data.Select(s => s.Id).ToList();

    private async Task<Session> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            return await new SessionService(Client()).GetAsync(sessionId,
                new SessionGetOptions { Expand = ["payment_intent.latest_charge"] }, cancellationToken: ct);
        }
        catch (StripeException ex)
        {
            throw new TicketingPlatformException($"Stripe could not read Checkout Session {sessionId}: {ex.Message}", ex);
        }
    }
}
