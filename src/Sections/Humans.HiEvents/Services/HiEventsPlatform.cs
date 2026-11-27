using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Humans.Base.Extensions;
using Humans.Stripe.Contracts;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace Humans.HiEvents.Services;

/// <summary>
/// <para>
/// Talks to the Nobodies fork of Hi.Events, which adds <c>POST /events/{id}/orders/checkout</c>: a reserved order at
/// caller-set prices with buyer and holder names filled in, returning a payment link, idempotent by
/// <c>external_reference</c> (the Humans order id). Refunds pass the Humans refund id as <c>idempotency_key</c>.
/// </para>
/// <para>
/// A platform order id is <c>{eventId}:{orderId}</c>, because every Hi.Events order route is scoped to its event.
/// Transport and unexpected HTTP failures throw <see cref="HttpRequestException"/>.
/// </para>
/// </summary>
internal sealed class HiEventsPlatform : ITicketingPlatform
{
    public const string PlatformKey = "hievents";
    /// <summary>The <c>Stripe:MirrorAccounts</c> entry for the account the fork's STRIPE_SECRET_KEY charges into.</summary>
    internal const string StripeAccountName = "Tickets";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static readonly HashSet<string> SupportedLocales =
        ["en", "de", "fr", "it", "nl", "es", "pt", "pl", "cs", "fi", "hu", "se", "sk", "tr", "el", "ja", "ko", "vi"];

    private readonly HttpClient _http;
    private readonly HiEventsSettings _settings;
    private readonly IClock _clock;
    private readonly ILogger<HiEventsPlatform> _logger;
    private readonly IStripeService _stripe;

    public HiEventsPlatform(
        HttpClient http,
        IOptions<HiEventsSettings> settings,
        IClock clock,
        ILogger<HiEventsPlatform> logger,
        IStripeService stripe)
    {
        _http = http;
        _settings = settings.Value;
        _clock = clock;
        _logger = logger;
        _stripe = stripe;

        // Never throw here: Tickets resolves every platform together, so a bad setting must only switch this one off.
        if (_settings.ApiBaseUri is { } baseUri)
        {
            _http.BaseAddress = baseUri;
        }

        if (_settings.IsConfigured)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        }

        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string Key => PlatformKey;

    public bool IsConfigured => _settings.IsConfigured;

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new TicketingPlatformException("Hi.Events is not configured.");
    }

    public async Task<PlatformOrderSession> CreateOrderAsync(PlatformOrderRequest request, CancellationToken ct = default)
    {
        EnsureConfigured();
        if (!int.TryParse(request.PlatformEventRef, NumberStyles.None, CultureInfo.InvariantCulture, out var eventId))
            throw new TicketingPlatformException($"Hi.Events needs a numeric event id, got '{request.PlatformEventRef}'.");

        var products = await GetProductsAsync(eventId, ct);
        if (products.FirstOrDefault(IsVisibleCarrier) is { } visible)
        {
            throw new TicketingPlatformException(
                $"Hi.Events product {visible.Id} on event {eventId} must be hidden, or it sells on the event's public page.");
        }

        var (firstName, lastName) = SplitBuyerName(request.BuyerName);
        var body = new CheckoutOrderBody(
            ExternalReference: request.OrderId.ToString("N"),
            FirstName: firstName,
            LastName: lastName,
            Email: request.BuyerEmail,
            Locale: ToLocale(request.Culture),
            ExpiresAt: InstantPattern.ExtendedIso.Format(request.ExpiresAt),
            ReturnUrl: request.ReturnUrl.AbsoluteUri,
            // Humans sends the buyer's ticket, receipt and refund emails itself.
            SendBuyerEmails: false,
            Items: request.Lines.Select(line => ToItem(line, eventId, products)).ToList());

        using var response = await _http.PostAsJsonAsync($"events/{eventId}/orders/checkout", body, JsonOptions, ct);

        if (response.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict)
        {
            throw new TicketingPlatformException(
                $"Hi.Events refused to create order {request.OrderId}: {await response.Content.ReadAsStringAsync(ct)}");
        }

        var order = await ReadOrderAsync(response, ct);

        if (!string.Equals(order.Currency, request.Currency, StringComparison.OrdinalIgnoreCase))
        {
            throw new TicketingPlatformException(
                $"Hi.Events event {eventId} sells in {order.Currency}, not {request.Currency}.");
        }

        if (ToMinor(order.TotalGross) != request.Lines.Sum(l => l.AmountMinor))
        {
            throw new TicketingPlatformException(
                $"Hi.Events order {order.Id} totals {order.TotalGross}, not the requested lines.");
        }

        var paymentUrl = order.PaymentLinkUrl
            ?? throw new HttpRequestException($"Hi.Events order {order.Id} came back without a payment link.");

        return new PlatformOrderSession(ToPlatformOrderId(eventId, order.Id), new Uri(paymentUrl), AdminUrl(eventId, order.Id));
    }

    public async Task<PlatformOrderStatus> GetStatusAsync(string platformOrderId, CancellationToken ct = default)
    {
        EnsureConfigured();
        var (eventId, orderId) = ParsePlatformOrderId(platformOrderId);
        var order = await GetOrderAsync(eventId, orderId, ct);
        var state = ToState(order);

        return new PlatformOrderStatus(
            State: state,
            FeeMinor: state == PlatformOrderState.Paid ? await FeeAsync(eventId, order, ct) : null,
            PaymentMethod: order.PaymentProvider?.ToLowerInvariant(),
            PaidAt: null,
            RefundedMinor: ToMinor(order.TotalRefunded ?? 0m),
            RefundFailed: string.Equals(order.RefundStatus, "REFUND_FAILED", StringComparison.Ordinal),
            ChargedBackMinor: ToMinor(order.TotalDisputed ?? 0m));
    }

    /// <summary>
    /// What Stripe kept on the order's payment, found by the <c>event_id</c> and <c>order_id</c> Hi.Events puts on every
    /// PaymentIntent, since its API does not expose the intent's id. Null while unknown: the reconciliation shows a fee
    /// Humans did not record.
    /// </summary>
    private Task<long?> FeeAsync(int eventId, HiEventsOrder order, CancellationToken ct) =>
        _stripe.FindAccountPaymentFeeAsync(StripeAccountName, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["event_id"] = eventId.ToString(CultureInfo.InvariantCulture),
            ["order_id"] = order.Id.ToString(CultureInfo.InvariantCulture),
        }, ParseCreatedAt(order.CreatedAt) ?? _clock.GetCurrentInstant(), ct);

    public async Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        EnsureConfigured();
        var (eventId, orderId) = ParsePlatformOrderId(request.PlatformOrderId);

        var body = new RefundBody(
            Amount: request.AmountMinor / 100m,
            NotifyBuyer: false,
            CancelOrder: false,
            IdempotencyKey: request.RefundId.ToString("N"));

        using var response = await _http.PostAsJsonAsync($"events/{eventId}/orders/{orderId}/refund", body, JsonOptions, ct);

        // A retry of this refund under its key succeeds in Hi.Events, so a 422 is always a refund that did not happen.
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var reason = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Hi.Events declined refund {RefundId} on {PlatformOrderId}: {Reason}",
                request.RefundId, request.PlatformOrderId, reason);
            return new RefundResult(RefundState.Failed, null, reason);
        }

        // A repeat of the key answers the refund already made under it, so this is that refund's state either way.
        var refund = (await ReadAsync<RefundEnvelope>(response, ct)).Meta?.Refund
            ?? throw new HttpRequestException($"Hi.Events accepted refund {request.RefundId} without naming it.");

        return refund.Status switch
        {
            "succeeded" => new RefundResult(RefundState.Succeeded, refund.Id, null),
            "pending" or "requires_action" => new RefundResult(RefundState.Pending, refund.Id, null),
            "failed" or "canceled" => new RefundResult(RefundState.Failed, refund.Id, $"Stripe refund {refund.Status}."),
            _ => throw new HttpRequestException($"Hi.Events reported refund {refund.Id} as '{refund.Status}'."),
        };
    }

    public async Task CancelAsync(string platformOrderId, CancellationToken ct = default)
    {
        EnsureConfigured();
        var (eventId, orderId) = ParsePlatformOrderId(platformOrderId);
        var order = await GetOrderAsync(eventId, orderId, ct);

        switch (ToState(order))
        {
            case PlatformOrderState.Paid:
                throw new TicketingPlatformException($"Hi.Events order {platformOrderId} is paid and cannot be cancelled.");
            case PlatformOrderState.Processing:
                throw new TicketingPlatformException($"Hi.Events order {platformOrderId} has a payment on its way and cannot be cancelled.");
            case PlatformOrderState.Cancelled:
                return;
        }

        using var response = await _http.PostAsJsonAsync(
            $"events/{eventId}/orders/{orderId}/cancel", new { refund = false }, JsonOptions, ct);

        if (response.StatusCode == HttpStatusCode.Conflict)
            return;

        await ReadOrderAsync(response, ct);
    }

    /// <summary>
    /// Hi.Events copies the buyer's email onto every holder's attendee record and Humans sends holders no email of their own, so
    /// the person cannot be told apart from the others on the order: the whole order is anonymised. Humans keeps the
    /// legal names it needs; amounts and statuses stay in Hi.Events.
    /// </summary>
    public async Task ErasePersonalDataAsync(string platformOrderId, string email, CancellationToken ct = default)
    {
        EnsureConfigured();
        var (eventId, orderId) = ParsePlatformOrderId(platformOrderId);
        using var response = await _http.PostAsync($"events/{eventId}/orders/{orderId}/anonymize", content: null, ct);
        await ReadOrderAsync(response, ct);
    }

    /// <summary>Anonymising an order already scrubs what Hi.Events keeps of it, so nothing is left per event.</summary>
    public Task EraseEventPersonalDataAsync(string? platformEventRef, CancellationToken ct = default) =>
        Task.CompletedTask;

    public PlatformSetup Setup => new(
        "Hi.Events",
        Hosting(),
        _settings.AppBaseUri is { } app ? new Uri(app, "manage/events") : null,
        [
            new("HiEvents:ApiUrl", "API root of our Hi.Events fork, ending in /api", IsSecret: false,
                !string.IsNullOrWhiteSpace(_settings.ApiUrl), string.IsNullOrWhiteSpace(_settings.ApiUrl) ? null : _settings.ApiUrl),
            new("HiEvents:AppUrl", "Web app root for order links; optional, defaults to ApiUrl without /api", IsSecret: false,
                !string.IsNullOrWhiteSpace(_settings.AppUrl), string.IsNullOrWhiteSpace(_settings.AppUrl) ? null : _settings.AppUrl),
            new("HIEVENTS_API_KEY", "API key of a user who can edit the event, see financials and refund", IsSecret: true,
                !string.IsNullOrEmpty(_settings.ApiKey), null),
            new("HIEVENTS_WEBHOOK_SECRET", "Signing secret of the webhook below", IsSecret: true,
                !string.IsNullOrEmpty(_settings.WebhookSecret), null),
        ],
        "/HiEvents/Webhook",
        [
            "Needs the Humans fork (peterdrier/Hi.Events): stock Hi.Events and Hi.Events Cloud have no checkout endpoint that takes Humans' prices.",
            "Each event needs exactly one TICKET and one GENERAL product, both hidden, without taxes or fees, offline payment off, and the event live. The event id in the ticketing settings is the numeric Hi.Events event id.",
            "The webhook goes on the order events.",
            "The fork's Stripe webhook must include payment_intent.processing and the charge.dispute.* events, or slow bank payments get cancelled at the hold and chargebacks never reach Humans.",
            $"Fees and payouts reconcile from the Stripe account behind the fork's STRIPE_SECRET_KEY: the shared Tickets Stripe:MirrorAccounts entry named {StripeAccountName} with that account's id and a read-only key (Balance, Balance transactions, Payouts, PaymentIntents, Charges, Refunds, Disputes read).",
        ]);

    private bool IsCloud => _settings.ApiBaseUri is { } api
        && (api.Host.Equals("hi.events", StringComparison.OrdinalIgnoreCase)
            || api.Host.EndsWith(".hi.events", StringComparison.OrdinalIgnoreCase));

    private string Hosting() => _settings.ApiBaseUri is not { } api
        ? "Not set"
        : IsCloud
            ? "Hi.Events Cloud (not supported: it lacks the fork's checkout endpoint)"
            : $"Self-hosted at {api.Host}";

    public async Task<IReadOnlyList<PlatformFinding>> CheckAsync(string? platformEventRef, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return [new(false, "HIEVENTS_API_KEY must be a nonblank valid HTTP authorization value, and HiEvents:ApiUrl and HiEvents:AppUrl (when set) must be absolute http(s) addresses.")];

        var findings = new List<PlatformFinding>
        {
            string.IsNullOrEmpty(_settings.WebhookSecret)
                ? new(false, "HIEVENTS_WEBHOOK_SECRET is unset: the webhook answers 503 and outcomes arrive only from Tickets' poll.")
                : new(true, "Webhook signing secret is set."),
        };
        findings.AddRange((await _stripe.CheckAccountAsync(StripeAccountName, ct)).Select(c => new PlatformFinding(c.Ok, c.Text)));
        if (IsCloud)
            findings.Add(new(false, "Hi.Events Cloud cannot sell Humans orders: it has no checkout endpoint taking Humans' prices."));
        try
        {
            using (var me = await _http.GetAsync("users/me", ct))
            {
                findings.Add(!me.IsSuccessStatusCode
                    ? new(false, $"Hi.Events answered {(int)me.StatusCode} to the API key.")
                    : await IsUserAsync(me, ct)
                        ? new(true, "The API key is accepted.")
                        : new(false, "users/me did not answer a Hi.Events user: check that HiEvents:ApiUrl ends in /api."));
            }

            if (string.IsNullOrWhiteSpace(platformEventRef) || !findings[^1].Ok)
                return findings;
            if (!int.TryParse(platformEventRef, NumberStyles.None, CultureInfo.InvariantCulture, out var eventId))
            {
                findings.Add(new(false, $"The event id must be the numeric Hi.Events event id, not '{platformEventRef}'."));
                return findings;
            }

            using (var ev = await _http.GetAsync($"events/{eventId}", ct))
            {
                if (!ev.IsSuccessStatusCode)
                {
                    findings.Add(new(false, $"Hi.Events answered {(int)ev.StatusCode} for event {eventId}."));
                    return findings;
                }

                var status = (await ev.Content.ReadFromJsonAsync<EventEnvelope>(JsonOptions, ct))?.Data?.Status;
                findings.Add(string.Equals(status, "LIVE", StringComparison.Ordinal)
                    ? new(true, "The event is live.")
                    : new(false, $"The event is {status ?? "in an unknown state"}; the pay page needs it live."));
            }

            var products = await GetProductsAsync(eventId, ct);
            foreach (var type in new[] { "TICKET", "GENERAL" })
            {
                var carriers = CarriersOf(products, type);
                findings.Add(carriers.Count == 1
                    ? new(true, $"One {type} carrier product found.")
                    : new(false, $"The event has {carriers.Count} {type} products; it needs exactly one."));
            }

            var visible = products.Where(IsVisibleCarrier).Select(p => p.Id).ToList();
            if (visible.Count > 0)
                findings.Add(new(false, $"Products {string.Join(", ", visible)} are visible and would sell on the event's public page; hide them."));

            // Fails validation before anything is made; tells the fork apart from stock Hi.Events.
            using var probe = await _http.PostAsJsonAsync($"events/{eventId}/orders/checkout", new { }, JsonOptions, ct);
            findings.Add(probe.StatusCode switch
            {
                HttpStatusCode.UnprocessableEntity => new(true, "The fork's checkout endpoint answers."),
                HttpStatusCode.Forbidden => new(false, "The API key's user cannot edit this event."),
                HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed =>
                    new(false, "This instance has no checkout endpoint: it is not the Humans fork."),
                _ => new(false, $"The checkout endpoint answered {(int)probe.StatusCode}."),
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            findings.Add(new(false, $"Hi.Events could not be read: {ex.Message}"));
        }

        return findings;
    }

    public string EventRefOf(string platformOrderId) =>
        ParsePlatformOrderId(platformOrderId).EventId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The Stripe account Hi.Events charges into; null while its mirror account is not configured. A movement's Humans
    /// order is the <c>external_reference</c> of the Hi.Events order its PaymentIntent names; an order the API key
    /// cannot see, or one Humans did not open, names none.
    /// </summary>
    public async Task<PaymentLedger?> GetPaymentLedgerAsync(Instant from, Instant to, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return null;
        StripeAccountLedger? stripe;
        try
        {
            stripe = await _stripe.GetAccountLedgerAsync(StripeAccountName, from, to, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new TicketingPlatformException(ex.Message, ex);
        }
        if (stripe is null)
            return null;

        var orderOf = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        var entries = new List<PaymentLedgerEntry>();
        foreach (var line in stripe.Lines)
        {
            Guid? humansOrderId = null;
            if (line.Payment is { } payment && !orderOf.TryGetValue(payment.PaymentIntentId, out humansOrderId))
                orderOf[payment.PaymentIntentId] = humansOrderId = await HumansOrderOfAsync(payment.Metadata, ct);
            entries.Add(new PaymentLedgerEntry(line.Id, line.CreatedAt, PaymentLedgerEntry.KindOf(line.ReportingCategory),
                line.AmountMinor, line.FeeMinor, humansOrderId, line.Description));
        }

        return new PaymentLedger(entries, stripe.Payouts.Select(p => new PaymentPayout(
            p.Id, p.CreatedAt, p.ArrivalAt, p.AmountMinor, p.Status, p.ContentsNetMinor, p.ContentsCount)).ToList());
    }

    private async Task<Guid?> HumansOrderOfAsync(IReadOnlyDictionary<string, string> metadata, CancellationToken ct)
    {
        if (!TryReadId(metadata, "event_id", out var eventId) || !TryReadId(metadata, "order_id", out var orderId))
            return null;
        using var response = await _http.GetAsync($"events/{eventId}/orders/{orderId}", ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            return null;
        var order = await ReadOrderAsync(response, ct);
        return Guid.TryParseExact(order.ExternalReference, "N", out var humansOrderId) ? humansOrderId : null;
    }

    private static bool TryReadId(IReadOnlyDictionary<string, string> metadata, string key, out int id)
    {
        id = 0;
        return metadata.TryGetValue(key, out var value)
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    public async Task<IReadOnlyList<PlatformOrderSummary>> ListOrdersAsync(
        string? platformEventRef, Instant from, Instant to, CancellationToken ct = default)
    {
        EnsureConfigured();
        if (!int.TryParse(platformEventRef, NumberStyles.None, CultureInfo.InvariantCulture, out var eventId))
            throw new TicketingPlatformException($"Hi.Events needs a numeric event id, got '{platformEventRef}'.");

        var range = string.Create(CultureInfo.InvariantCulture,
            $"filter_fields[created_at][gte]={Uri.EscapeDataString(from.ToInvariantTimestamp())}&filter_fields[created_at][lt]={Uri.EscapeDataString(to.ToInvariantTimestamp())}");
        var orders = new Dictionary<int, PlatformOrderSummary>();
        for (var page = 1; page <= PageSafetyCap; page++)
        {
            using var response = await _http.GetAsync(string.Create(CultureInfo.InvariantCulture,
                $"events/{eventId}/orders?per_page={ListPageSize}&page={page}&sort_by=created_at&sort_direction=asc&{range}"), ct);
            var read = await ReadAsync<OrderPage>(response, ct);
            var meta = read.Meta ?? throw new HttpRequestException("Hi.Events answered an order page without its paging.");
            if (meta.LastPage < 1)
                throw new HttpRequestException("Hi.Events answered an order page with invalid paging.");
            foreach (var order in read.Data ?? [])
                orders[order.Id] = new PlatformOrderSummary(
                ToPlatformOrderId(eventId, order.Id),
                Guid.TryParseExact(order.ExternalReference, "N", out var humansOrderId) ? humansOrderId : null,
                ParseCreatedAt(order.CreatedAt)
                    ?? throw new HttpRequestException($"Hi.Events order {order.Id} has an unreadable created_at '{order.CreatedAt}'."),
                ToState(order),
                ToMinor(order.TotalGross),
                order.Attendees?.Count,
                ToMinor(order.TotalRefunded ?? 0m),
                ToMinor(order.TotalDisputed ?? 0m));
            if (page < meta.LastPage)
                continue;
            // Orders sharing a created_at second may change places between pages; one skipped shows here.
            if (orders.Count != meta.Total)
                throw new HttpRequestException($"Hi.Events listed {orders.Count} distinct orders of the {meta.Total} it counts; read again.");
            return orders.Values.ToList();
        }
        throw new HttpRequestException($"Hi.Events orders hit the {PageSafetyCap}-page safety cap.");
    }

    private const int ListPageSize = 100;
    private const int PageSafetyCap = 500;

    /// <summary>Laravel serialises timestamp columns as ISO 8601 in UTC; a raw column reads as plain UTC text.</summary>
    private static Instant? ParseCreatedAt(string? value) =>
        value is not null && InstantPattern.ExtendedIso.Parse(value) is { Success: true } iso
            ? iso.Value
            : DateFormattingExtensions.ParseInvariantTimestamp(value);

    private static async Task<bool> IsUserAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return (await response.Content.ReadFromJsonAsync<UserEnvelope>(JsonOptions, ct))?.Data?.Id is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private PlatformOrderState ToState(HiEventsOrder order) => order.Status switch
    {
        "COMPLETED" when string.Equals(order.PaymentStatus, "PAYMENT_RECEIVED", StringComparison.Ordinal) => PlatformOrderState.Paid,
        "COMPLETED" => PlatformOrderState.Failed,
        "CANCELLED" => PlatformOrderState.Cancelled,
        // The buyer finished a method that settles later (a SEPA debit): the money is on its way, whatever the hold.
        "RESERVED" or "ABANDONED" when order.PaymentProcessingAt is not null => PlatformOrderState.Processing,
        "ABANDONED" => PlatformOrderState.Expired,
        "RESERVED" => DateFormattingExtensions.ParseInvariantTimestamp(order.ReservedUntil) is { } until
            ? until <= _clock.GetCurrentInstant() ? PlatformOrderState.Expired : PlatformOrderState.Pending
            : throw new HttpRequestException($"Hi.Events order {order.Id} has an unreadable reserved_until '{order.ReservedUntil}'."),
        // Only reachable when offline payment is on for the event, which it must not be; the order is still unpaid.
        "AWAITING_OFFLINE_PAYMENT" => PlatformOrderState.Pending,
        _ => throw new HttpRequestException($"Hi.Events order {order.Id} has unknown status '{order.Status}'."),
    };

    private Uri? AdminUrl(int eventId, int orderId) =>
        _settings.AppBaseUri is { } app
            ? new Uri(app, string.Create(CultureInfo.InvariantCulture, $"manage/event/{eventId}/orders#order-{orderId}"))
            : null;

    private async Task<IReadOnlyList<HiEventsProduct>> GetProductsAsync(int eventId, CancellationToken ct)
    {
        var products = new List<HiEventsProduct>();
        for (var page = 1; page <= PageSafetyCap; page++)
        {
            using var response = await _http.GetAsync(string.Create(CultureInfo.InvariantCulture,
                $"events/{eventId}/products?per_page={ListPageSize}&page={page}"), ct);
            var read = await ReadAsync<ProductPage>(response, ct);
            var meta = read.Meta ?? throw new HttpRequestException("Hi.Events answered a product page without its paging.");
            if (meta.LastPage < 1)
                throw new HttpRequestException("Hi.Events answered a product page with invalid paging.");
            products.AddRange(read.Data ?? throw new HttpRequestException("Hi.Events answered a product page without data."));
            if (page >= meta.LastPage)
                return products;
        }
        throw new HttpRequestException($"Hi.Events products hit the {PageSafetyCap}-page safety cap.");
    }

    /// <summary>
    /// Humans owns the catalogue and prices every line, so a Hi.Events event needs just one carrier product per line
    /// kind: one product of type <c>TICKET</c> for ticket lines and one of type <c>GENERAL</c> for donations. Neither may
    /// carry taxes or fees in Hi.Events, or the order total stops matching the lines and the order is refused. Both must
    /// be hidden: the pay page needs the event live, and a visible carrier would sell on its public page at its base price.
    /// </summary>
    private static CheckoutOrderItem ToItem(PlatformOrderLine line, int eventId, IReadOnlyList<HiEventsProduct> products)
    {
        var productType = line.Kind == OrderLineKind.Ticket ? "TICKET" : "GENERAL";
        var carriers = CarriersOf(products, productType);
        if (carriers.Count != 1)
        {
            throw new TicketingPlatformException(
                $"Hi.Events event {eventId} needs exactly one {productType} product to carry {line.Kind} lines; it has {carriers.Count}.");
        }

        return new CheckoutOrderItem(
            ProductId: carriers[0].Id,
            Price: line.AmountMinor / 100m,
            Attendee: line.HolderName is null ? null : new CheckoutAttendee(line.HolderName));
    }

    private static List<HiEventsProduct> CarriersOf(IReadOnlyList<HiEventsProduct> products, string productType) =>
        products.Where(p => string.Equals(p.ProductType, productType, StringComparison.Ordinal)).ToList();

    private static bool IsVisibleCarrier(HiEventsProduct product) =>
        product.ProductType is "TICKET" or "GENERAL" && product.IsHidden != true;

    private async Task<HiEventsOrder> GetOrderAsync(int eventId, int orderId, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"events/{eventId}/orders/{orderId}", ct);
        return await ReadOrderAsync(response, ct);
    }

    private static async Task<HiEventsOrder> ReadOrderAsync(HttpResponseMessage response, CancellationToken ct) =>
        (await ReadAsync<Envelope>(response, ct)).Data
            ?? throw new HttpRequestException("Hi.Events returned an order response without data.");

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct) where T : class
    {
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Hi.Events returned {(int)response.StatusCode}: {text}", null, response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct)
            ?? throw new HttpRequestException("Hi.Events returned an empty response.");
    }

    private static string ToLocale(string culture)
    {
        var language = culture.Split('-', 2)[0].ToLowerInvariant();
        if (string.Equals(language, "ca", StringComparison.Ordinal))
        {
            return "es";
        }

        return SupportedLocales.Contains(language) ? language : "en";
    }

    /// <summary>
    /// Hi.Events keeps a buyer's first and last name in 50 characters each. A longer full name is split at a space so
    /// it still reads whole; past 100 characters the tail is cut, since the ticket line carries the holder's full
    /// legal name.
    /// </summary>
    internal static (string FirstName, string? LastName) SplitBuyerName(string name)
    {
        name = name.Trim();
        var firstLength = name.EnumerateRunes().Take(BuyerNamePartLength).Sum(rune => rune.Utf16SequenceLength);
        if (name.Length == firstLength)
            return (name, null);

        var split = name.LastIndexOf(' ', firstLength);
        if (split <= 0)
            split = firstLength;

        var last = name[split..].Trim();
        var lastLength = last.EnumerateRunes().Take(BuyerNamePartLength).Sum(rune => rune.Utf16SequenceLength);
        return (name[..split].Trim(), last[..lastLength].TrimEnd());
    }

    private const int BuyerNamePartLength = 50;

    private static long ToMinor(decimal amount) => (long)decimal.Round(amount * 100m, MidpointRounding.AwayFromZero);

    internal static string ToPlatformOrderId(int eventId, int orderId) =>
        string.Create(CultureInfo.InvariantCulture, $"{eventId}:{orderId}");

    internal static (int EventId, int OrderId) ParsePlatformOrderId(string platformOrderId)
    {
        var parts = platformOrderId.Split(':');
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var eventId)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var orderId))
        {
            return (eventId, orderId);
        }

        throw new ArgumentException($"'{platformOrderId}' is not a Hi.Events order id.", nameof(platformOrderId));
    }

    private sealed record CheckoutOrderBody(
        string ExternalReference,
        string FirstName,
        string? LastName,
        string Email,
        string Locale,
        string ExpiresAt,
        string ReturnUrl,
        bool SendBuyerEmails,
        IReadOnlyList<CheckoutOrderItem> Items);

    private sealed record CheckoutOrderItem(int ProductId, decimal Price, CheckoutAttendee? Attendee);

    private sealed record CheckoutAttendee(string FirstName);

    private sealed record RefundBody(decimal Amount, bool NotifyBuyer, bool CancelOrder, string IdempotencyKey);

    private sealed record Envelope(HiEventsOrder? Data);

    private sealed record EventEnvelope(EventStatus? Data);

    private sealed record UserEnvelope(UserId? Data);

    private sealed record UserId(int? Id);

    private sealed record EventStatus(string? Status);

    private sealed record ProductPage(List<HiEventsProduct>? Data, PageMeta? Meta);

    private sealed record OrderPage(List<HiEventsOrder>? Data, PageMeta? Meta);

    private sealed record PageMeta(int LastPage, int Total);

    private sealed record HiEventsAttendee(int Id);

    private sealed record HiEventsProduct(int Id, string? ProductType, bool? IsHidden);

    private sealed record RefundEnvelope(RefundMeta? Meta);

    private sealed record RefundMeta(StripeRefund? Refund);

    private sealed record StripeRefund(string Id, string Status);

    private sealed record HiEventsOrder(
        int Id,
        string Status,
        string? PaymentStatus,
        string? RefundStatus,
        string Currency,
        decimal TotalGross,
        decimal? TotalRefunded,
        string? PaymentProvider,
        string? PaymentLinkUrl,
        string? ReservedUntil, // UTC, "yyyy-MM-dd HH:mm:ss" with no offset
        string? ExternalReference = null,
        string? CreatedAt = null,
        List<HiEventsAttendee>? Attendees = null,
        decimal? TotalDisputed = null,
        string? PaymentProcessingAt = null);
}
