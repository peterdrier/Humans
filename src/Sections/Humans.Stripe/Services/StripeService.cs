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
    /// <summary>STRIPE_TICKETS_KEY: fee reads and Box Office checkout/refunds.</summary>
    public string TicketsKey { get; set; } = string.Empty;

    /// <summary>STRIPE_STORE_KEY: <c>checkout_session:write</c>, which includes read, so it can also list sessions.</summary>
    public string StoreKey { get; set; } = string.Empty;

    public string StoreWebhookSecret { get; set; } = string.Empty;

    /// <summary>STRIPE_STORE_WEBHOOK_REGISTRAR_KEY (<c>webhook_endpoint:read/write</c>). PR previews only; never QA or production.</summary>
    public string WebhookRegistrarKey { get; set; } = string.Empty;

    public bool IsConfigured => IsUsableApiKey(TicketsKey);

    public bool IsStoreCheckoutConfigured => IsUsableApiKey(StoreKey);

    public bool IsStoreWebhookConfigured => !string.IsNullOrEmpty(StoreWebhookSecret);

    public bool IsWebhookRegistrarConfigured => IsUsableApiKey(WebhookRegistrarKey);

    internal static bool IsUsableApiKey(string? key) =>
        !string.IsNullOrEmpty(key) && !key.Any(char.IsWhiteSpace);

    /// <summary>GitHub owner whose open-PR list the registrar's sweep reads.</summary>
    public string WebhookCleanupGitHubOwner { get; set; } = string.Empty;

    public string WebhookCleanupGitHubRepository { get; set; } = string.Empty;

    public bool IsWebhookCleanupConfigured =>
        !string.IsNullOrEmpty(WebhookCleanupGitHubOwner) && !string.IsNullOrEmpty(WebhookCleanupGitHubRepository);

}

internal sealed class StripeService(
    IOptions<StripeSettings> settings,
    ILogger<StripeService> logger,
    IHttpClient? httpClient = null,
    IOptions<StripeMirrorOptions>? mirrorOptions = null,
    IClock? clock = null) : IStripeService
{
    /// <summary>The <c>Stripe:MirrorAccounts</c> entry naming the Store account; refunds are read through its key.</summary>
    internal const string StoreAccountName = "Store";

    private readonly StripeSettings _settings = settings.Value;

    private StripeMirrorAccount? StoreAccount => mirrorOptions?.Value.Accounts
        .FirstOrDefault(a => string.Equals(a.Name, StoreAccountName, StringComparison.Ordinal));

    public bool IsConfigured => _settings.IsConfigured;

    public bool IsStoreCheckoutConfigured => _settings.IsStoreCheckoutConfigured;

    public bool IsStoreWebhookConfigured => _settings.IsStoreWebhookConfigured;

    /// <summary>Half a cent rounds away from zero.</summary>
    public static long ToStripeMinorUnits(decimal amountEur) =>
        (long)Math.Round(amountEur * 100m, MidpointRounding.AwayFromZero);

    public static decimal FromStripeMinorUnits(long cents) => cents / 100m;

    public async Task<StoreCheckoutSessionData> CreateCheckoutSessionAsync(
        Guid storeOrderId,
        decimal amountEur,
        string successUrl,
        string cancelUrl,
        string? customerEmail,
        string lineItemDescription,
        CancellationToken ct = default,
        Guid? checkoutAttemptId = null,
        string? supersededSessionId = null)
    {
        using var _ = logger.TimeOperation();
        if (amountEur <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountEur), "Checkout amount must be positive.");
        if (!_settings.IsStoreCheckoutConfigured)
            throw new InvalidOperationException("Stripe Store key is not configured.");

        if (supersededSessionId is not null)
        {
            var superseded = await ExpireCheckoutSessionAsync(supersededSessionId, ct);
            // Already completed: a replacement would be a second payable checkout for the same balance.
            if (string.Equals(superseded.Status, "complete", StringComparison.Ordinal))
                return superseded;
        }

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
            var requestOptions = checkoutAttemptId is { } attemptId
                ? new RequestOptions { IdempotencyKey = $"humans_store_checkout_{attemptId}" }
                : null;
            var session = await sessions.CreateAsync(options, requestOptions, ct);
            // A replay returns the cached creation response, which always says open; the attempt's caller needs the
            // session's live state.
            if (requestOptions is not null)
                session = await sessions.GetAsync(session.Id, cancellationToken: ct);
            return MapSession(session) ?? throw new InvalidOperationException(
                $"Stripe created Checkout Session {session.Id} without EUR currency.");
        }
        catch (StripeException ex) when (StripeStartupSmokeService.IsPermissionError(ex))
        {
            logger.LogWarning(
                "Stripe permission_error creating Checkout Session for order {OrderId}: the Store key is missing checkout_session:write scope. {Message}",
                storeOrderId, ex.Message);
            throw;
        }
    }

    /// <summary>A session that is no longer open comes back as Stripe holds it; an open one Stripe won't expire throws.</summary>
    private async Task<StoreCheckoutSessionData> ExpireCheckoutSessionAsync(string sessionId, CancellationToken ct)
    {
        var sessions = new SessionService(new StripeClient(_settings.StoreKey, httpClient: httpClient));
        Session session;
        try
        {
            session = await sessions.ExpireAsync(sessionId, cancellationToken: ct);
        }
        catch (StripeException ex) when (!StripeStartupSmokeService.IsPermissionError(ex))
        {
            // Stripe refuses to expire a session that is no longer open; its current state is the answer.
            session = await sessions.GetAsync(sessionId, cancellationToken: ct);
            if (string.Equals(session.Status, "open", StringComparison.Ordinal))
                throw;
        }

        return MapSession(session) ?? throw new InvalidOperationException(
            $"Stripe Checkout Session {session.Id} has no EUR currency.");
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

    public async Task<IReadOnlyList<StoreRefundData>?> ListStoreRefundsAsync(
        string paymentIntentId, CancellationToken ct = default)
    {
        using var _ = logger.TimeOperation();
        if (StoreAccount is not { IsConfigured: true } account)
        {
            logger.LogWarning("Store refunds for {PaymentIntentId} requested without a configured Store mirror account; Stripe not queried.",
                paymentIntentId);
            return null;
        }

        var client = new StripeClient(account.ApiKey, httpClient: httpClient);
        var request = account.UsesOrgKey ? new RequestOptions { StripeContext = account.Id } : new RequestOptions();
        var results = new List<StoreRefundData>();
        try
        {
            await foreach (var r in new RefundService(client).ListAutoPagingAsync(
                               new RefundListOptions { PaymentIntent = paymentIntentId, Limit = 100 }, request, ct))
            {
                if (!string.Equals(r.Currency, "eur", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Stripe refund {r.Id} does not declare EUR currency.");
                results.Add(new StoreRefundData(
                    r.Id,
                    paymentIntentId,
                    FromStripeMinorUnits(r.Amount),
                    r.Status ?? "",
                    Instant.FromDateTimeUtc(DateTime.SpecifyKind(r.Created, DateTimeKind.Utc))));
            }
        }
        catch (StripeException ex) when (StripeStartupSmokeService.IsPermissionError(ex))
        {
            logger.LogWarning(
                "Stripe permission_error listing refunds for {PaymentIntentId}: the Store account's read key is missing refund read scope. {Message}",
                paymentIntentId, ex.Message);
            return null;
        }

        return results;
    }

    public async Task<StoreChargeData?> GetStoreChargeAsync(string paymentIntentId, CancellationToken ct = default)
    {
        using var _ = logger.TimeOperation();
        if (StoreAccount is not { IsConfigured: true } account)
        {
            logger.LogWarning("Store charge for {PaymentIntentId} requested without a configured Store mirror account; Stripe not queried.",
                paymentIntentId);
            return null;
        }

        var client = new StripeClient(account.ApiKey, httpClient: httpClient);
        var request = account.UsesOrgKey ? new RequestOptions { StripeContext = account.Id } : new RequestOptions();
        try
        {
            var pi = await new PaymentIntentService(client).GetAsync(
                paymentIntentId, new PaymentIntentGetOptions { Expand = ["latest_charge"] }, request, ct);
            if (pi.LatestCharge is not { } charge) return null;
            if (!string.Equals(charge.Currency, "eur", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Stripe charge {charge.Id} does not declare EUR currency.");
            return new StoreChargeData(
                FromStripeMinorUnits(charge.Amount),
                Instant.FromDateTimeUtc(DateTime.SpecifyKind(charge.Created, DateTimeKind.Utc)));
        }
        catch (StripeException ex) when (StripeStartupSmokeService.IsPermissionError(ex))
        {
            logger.LogWarning(
                "Stripe permission_error reading the charge of {PaymentIntentId}: the Store account's read key is missing payment read scope. {Message}",
                paymentIntentId, ex.Message);
            return null;
        }
    }

    public string StorePaymentDashboardUrl(string paymentIntentId)
    {
        var id = Uri.EscapeDataString(paymentIntentId);
        return StoreAccount is { Id: { Length: > 0 } accountId }
            ? $"https://dashboard.stripe.com/{Uri.EscapeDataString(accountId)}/payments/{id}"
            : $"https://dashboard.stripe.com/payments/{id}";
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
            CreatedAt: Instant.FromDateTimeUtc(DateTime.SpecifyKind(s.Created, DateTimeKind.Utc)),
            Status: s.Status,
            Url: string.IsNullOrEmpty(s.Url) ? null : s.Url);
    }

    // Live reads of a named mirror account, for a ticketing platform that charges into its own Stripe account.
    private static readonly Duration SearchLag = Duration.FromHours(2);

    private StripeMirrorAccount? Account(string name) => mirrorOptions?.Value.Accounts
        .FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));

    private (StripeClient Client, RequestOptions Request)? Reader(string name) => Account(name) is { IsConfigured: true } account
        ? (new StripeClient(account.ApiKey, httpClient: httpClient),
            account.UsesOrgKey ? new RequestOptions { StripeContext = account.Id } : new RequestOptions())
        : null;

    public async Task<StripeAccountLedger?> GetAccountLedgerAsync(
        string accountName, Instant from, Instant to, CancellationToken ct = default)
    {
        using var _ = logger.TimeOperation();
        if (Reader(accountName) is not var (client, request))
            return null;
        try
        {
            var period = new DateRangeOptions { GreaterThanOrEqual = from.ToDateTimeUtc(), LessThan = to.ToDateTimeUtc() };
            var transactions = new List<BalanceTransaction>();
            await foreach (var t in new BalanceTransactionService(client).ListAutoPagingAsync(
                               new BalanceTransactionListOptions { Created = period, Limit = 100, Expand = ["data.source"] },
                               request, ct))
                transactions.Add(t);

            // A refund or dispute usually names an intent from before the period: list from a day earlier, read the rest.
            var intents = new Dictionary<string, PaymentIntent>(StringComparer.Ordinal);
            await foreach (var intent in new PaymentIntentService(client).ListAutoPagingAsync(
                               new PaymentIntentListOptions
                               {
                                   Created = new DateRangeOptions
                                   {
                                       GreaterThanOrEqual = (from - Duration.FromDays(1)).ToDateTimeUtc(),
                                       LessThan = to.ToDateTimeUtc(),
                                   },
                                   Limit = 100,
                               },
                               request, ct))
                intents[intent.Id] = intent;

            var lines = new List<StripeLedgerLine>();
            foreach (var t in transactions)
            {
                var (intentId, sourceMetadata) = t.Source switch
                {
                    Charge c => (c.PaymentIntentId, c.Metadata),
                    Refund r => (r.PaymentIntentId, r.Metadata),
                    Dispute d => (d.PaymentIntentId, d.Metadata),
                    _ => (null, null),
                };
                StripeLedgerPayment? payment = null;
                if (intentId is not null)
                {
                    if (!intents.TryGetValue(intentId, out var intent))
                        intents[intentId] = intent = await new PaymentIntentService(client).GetAsync(intentId, null, request, ct);
                    var metadata = new Dictionary<string, string>(intent.Metadata ?? [], StringComparer.Ordinal);
                    // A movement can carry its order reference even when the older intent does not.
                    if (sourceMetadata is not null)
                        foreach (var pair in sourceMetadata)
                            metadata[pair.Key] = pair.Value;
                    payment = new StripeLedgerPayment(intent.Id, intent.Description, metadata);
                }

                lines.Add(new StripeLedgerLine(t.Id, InstantOf(t.Created), t.ReportingCategory ?? "", t.Amount, t.Fee,
                    t.Description, payment));
            }

            var payouts = new List<StripeLedgerPayout>();
            await foreach (var payout in new PayoutService(client).ListAutoPagingAsync(
                               new PayoutListOptions { Created = period, Limit = 100 }, request, ct))
            {
                long? contentsNet = null;
                var contentsCount = 0;
                // Stripe lists a payout's contents only for automatic payouts, and in full only once it has finished
                // reconciling them; a partial list would read as a mismatch.
                if (payout.Automatic && string.Equals(payout.ReconciliationStatus, "completed", StringComparison.Ordinal))
                {
                    contentsNet = 0;
                    await foreach (var t in new BalanceTransactionService(client).ListAutoPagingAsync(
                                       new BalanceTransactionListOptions { Payout = payout.Id, Limit = 100 }, request, ct))
                    {
                        if (string.Equals(t.Type, "payout", StringComparison.Ordinal))
                            continue;
                        contentsNet += t.Net;
                        contentsCount++;
                    }
                }

                payouts.Add(new StripeLedgerPayout(payout.Id, InstantOf(payout.Created), InstantOf(payout.ArrivalDate),
                    payout.Amount, payout.Status ?? "", contentsNet, contentsCount));
            }

            return new StripeAccountLedger(lines, payouts);
        }
        catch (StripeException ex)
        {
            throw new InvalidOperationException($"Stripe refused reading the {accountName} account: {ex.Message}", ex);
        }
    }

    public Task<long?> GetAccountPaymentFeeAsync(string accountName, string paymentIntentId, CancellationToken ct = default) =>
        FeeAsync(accountName, paymentIntentId, async (client, request) => await new PaymentIntentService(client).GetAsync(
            paymentIntentId, new PaymentIntentGetOptions { Expand = ["latest_charge.balance_transaction"] }, request, ct), ct);

    public Task<long?> FindAccountPaymentFeeAsync(
        string accountName, IReadOnlyDictionary<string, string> metadata, Instant createdSince, CancellationToken ct = default) =>
        FeeAsync(accountName, string.Join(", ", metadata.Select(m => $"{m.Key}={m.Value}")), async (client, request) =>
        {
            var service = new PaymentIntentService(client);
            var query = string.Join(" AND ", metadata.Select(m => $"metadata['{Quoted(m.Key)}']:'{Quoted(m.Value)}'"));
            var found = (await service.SearchAsync(new PaymentIntentSearchOptions
            {
                Query = $"{query} AND status:'succeeded'",
                Expand = ["data.latest_charge.balance_transaction"],
            }, request, ct)).Data.FirstOrDefault();
            if (found is not null || (clock ?? SystemClock.Instance).GetCurrentInstant() - createdSince > SearchLag)
                return found;

            await foreach (var intent in service.ListAutoPagingAsync(new PaymentIntentListOptions
            {
                Created = new DateRangeOptions { GreaterThanOrEqual = (createdSince - Duration.FromMinutes(1)).ToDateTimeUtc() },
                Limit = 100,
                Expand = ["data.latest_charge.balance_transaction"],
            }, request, ct))
            {
                if (string.Equals(intent.Status, "succeeded", StringComparison.Ordinal)
                    && metadata.All(m => intent.Metadata is not null && intent.Metadata.TryGetValue(m.Key, out var v)
                        && string.Equals(v, m.Value, StringComparison.Ordinal)))
                    return intent;
            }

            return null;
        }, ct);

    private async Task<long?> FeeAsync(
        string accountName, string what, Func<StripeClient, RequestOptions, Task<PaymentIntent?>> read, CancellationToken ct)
    {
        using var _ = logger.TimeOperation();
        if (Reader(accountName) is not var (client, request))
            return null;
        try
        {
            return (await read(client, request))?.LatestCharge?.BalanceTransaction?.Fee;
        }
        catch (Exception ex) when (ex is StripeException or HttpRequestException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Could not read the Stripe fee of {What} on the {Account} account", what, accountName);
            return null;
        }
    }

    public async Task<IReadOnlyList<StripeAccountCheck>> CheckAccountAsync(
        string accountName, CancellationToken ct = default, bool includeCheckout = false)
    {
        var findings = new List<StripeAccountCheck>();
        if (includeCheckout)
        {
            var checkoutMode = _settings.TicketsKey.StartsWith("sk_test_", StringComparison.Ordinal)
                || _settings.TicketsKey.StartsWith("rk_test_", StringComparison.Ordinal) ? "test mode" : "live mode";
            findings.Add(_settings.IsConfigured
                ? await ProbeAsync("STRIPE_TICKETS_KEY", $"Checkout Sessions ({checkoutMode})",
                    () => new SessionService(new StripeClient(_settings.TicketsKey, httpClient: httpClient))
                        .ListAsync(new SessionListOptions { Limit = 1 }, cancellationToken: ct), ct)
                : new(false, "STRIPE_TICKETS_KEY is unset or contains whitespace: checkout cannot be read."));
        }
        var account = Account(accountName);
        if (account is null)
        {
            findings.Add(new(false, $"Stripe:MirrorAccounts has no {accountName} entry: its Stripe fees and payouts are not reconciled."));
            return findings;
        }
        var keyName = account.UsesOrgKey ? StripeMirrorConfiguration.OrgKeyVariable : account.KeyVariable!;
        if (Reader(accountName) is not var (client, request))
        {
            findings.Add(new(false, $"{keyName} is unset or contains whitespace: the {accountName} account's Stripe fees and payouts are not reconciled."));
            return findings;
        }

        var mode = account.ApiKey.StartsWith("sk_test_", StringComparison.Ordinal) || account.ApiKey.StartsWith("rk_test_", StringComparison.Ordinal)
            ? "test mode"
            : "live mode";
        // memory/code/stripe-restricted-keys.md: a probe can only confirm scopes are present, not that nothing else is.
        if (account.ApiKey.StartsWith("sk_live_", StringComparison.Ordinal))
            findings.Add(new(false, $"{keyName} is a full secret key that can move money: use a read-only restricted key (rk_live_)."));
        findings.AddRange(
        [
            await ProbeAsync(keyName, $"Balance transactions ({mode})",
                () => new BalanceTransactionService(client).ListAsync(new BalanceTransactionListOptions { Limit = 1 }, request, ct), ct),
            await ProbeAsync(keyName, "Payouts",
                () => new PayoutService(client).ListAsync(new PayoutListOptions { Limit = 1 }, request, ct), ct),
            await ProbeAsync(keyName, "PaymentIntents",
                () => new PaymentIntentService(client).ListAsync(new PaymentIntentListOptions { Limit = 1 }, request, ct), ct),
            // The ledger expands each movement's charge, refund or dispute, and a fee read its payment's charge.
            await ProbeAsync(keyName, "Charges",
                () => new ChargeService(client).ListAsync(new ChargeListOptions { Limit = 1 }, request, ct), ct),
            await ProbeAsync(keyName, "Refunds",
                () => new RefundService(client).ListAsync(new RefundListOptions { Limit = 1 }, request, ct), ct),
            await ProbeAsync(keyName, "Disputes",
                () => new DisputeService(client).ListAsync(new DisputeListOptions { Limit = 1 }, request, ct), ct),
        ]);
        return findings;
    }

    private static async Task<StripeAccountCheck> ProbeAsync(string keyName, string what, Func<Task> read, CancellationToken ct)
    {
        try
        {
            await read();
            return new(true, $"{keyName} reads {what}.");
        }
        catch (StripeException ex)
        {
            return new(false, $"Stripe refused {keyName} reading {what} ({(int)ex.HttpStatusCode} {ex.StripeError?.Code ?? ex.StripeError?.Type ?? "error"}).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return new(false, $"Stripe could not be reached: {ex.Message}");
        }
    }

    private static string Quoted(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    private static Instant InstantOf(DateTime utc) => Instant.FromDateTimeUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}
