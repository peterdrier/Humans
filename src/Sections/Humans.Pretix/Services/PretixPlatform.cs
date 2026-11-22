using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Humans.Stripe.Contracts;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace Humans.Pretix.Services;

/// <summary>
/// <para>
/// Fills Tickets' checkout port with stock pretix (2026.9): an order created through the REST API at Humans' prices,
/// paid by the buyer on pretix's own order page, which sends them back to <see cref="PlatformOrderRequest.ReturnUrl"/>
/// through pretix's bundled <c>returnurl</c> plugin.
/// </para>
/// <para>
/// The platform order id is <c>{event slug}/{order code}</c>, because pretix addresses an order by both and the port
/// passes only the id. The code is derived from <see cref="PlatformOrderRequest.OrderId"/>, so a retried create finds the
/// order it already made instead of making a second one.
/// </para>
/// </summary>
internal sealed partial class PretixPlatform(
    HttpClient http,
    IOptions<PretixSettings> options,
    ILogger<PretixPlatform> logger,
    IStripeService stripe) : ITicketingPlatform
{
    public const string PlatformKey = "pretix";
    /// <summary>The <c>Stripe:MirrorAccounts</c> entry for the account pretix's Stripe settings charge into.</summary>
    internal const string StripeAccountName = "Pretix";

    /// <summary>
    /// Internal names of the carrier items every pretix event needs: one admission item taxed like a ticket, one
    /// non-admission item without tax, both "free price". pretix items belong to one event, so they are looked up per
    /// event rather than configured.
    /// </summary>
    internal const string TicketItemName = "humans-ticket";
    internal const string DonationItemName = "humans-donation";

    private const string HumansOrderIdMeta = "humans_order_id";
    private const string RefundCommentPrefix = "humans-refund:";

    // pretix accepts A-Z and 0-9 without I and O; dropping 0 and 1 too leaves 32 symbols, five bits each.
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 16;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private PretixSettings Settings => options.Value;

    public string Key => PlatformKey;

    public bool IsConfigured => Settings.IsConfigured;

    public async Task<PlatformOrderSession> CreateOrderAsync(PlatformOrderRequest request, CancellationToken ct = default)
    {
        EnsureConfigured();
        var eventSlug = request.PlatformEventRef;
        if (string.IsNullOrWhiteSpace(eventSlug))
            throw new TicketingPlatformException("pretix needs the event's slug as PlatformEventRef.");
        Validate(request);

        var ev = await SendAsync<PretixEvent>(HttpMethod.Get, EventPath(eventSlug), body: null, ct);
        if (!string.Equals(ev.Currency, request.Currency, StringComparison.OrdinalIgnoreCase))
            throw new TicketingPlatformException(
                $"pretix event {eventSlug} sells in {ev.Currency}, not {request.Currency}.");

        var items = await SendAsync<PretixPage<PretixItem>>(
            HttpMethod.Get, $"{EventPath(eventSlug)}items/?active=true", body: null, ct);
        var code = OrderCode(request.OrderId);
        var body = BuildOrderBody(request, code, CarrierItem(items, TicketItemName), CarrierItem(items, DonationItemName));
        var created = true;

        var response = await PostOrderAsync(eventSlug, body, ct);
        if (response.Status == HttpStatusCode.BadRequest && HasError(response.Body, "locale"))
        {
            // The buyer's language is not one the event offers; pretix then uses the event default.
            body.Remove("locale");
            response = await PostOrderAsync(eventSlug, body, ct);
        }

        PretixOrder order;
        if (response.Status == HttpStatusCode.BadRequest && HasError(response.Body, "code"))
        {
            order = await SendAsync<PretixOrder>(HttpMethod.Get, OrderPath(eventSlug, code), body: null, ct);
            if (order.ApiMeta?.TryGetValue(HumansOrderIdMeta, out var owner) != true
                || !string.Equals(owner.ToString(), request.OrderId.ToString(), StringComparison.Ordinal))
            {
                throw new TicketingPlatformException(
                    $"pretix order code {code} already belongs to another order.");
            }

            created = false;
        }
        else if (response.Status == HttpStatusCode.BadRequest)
        {
            throw new TicketingPlatformException($"pretix refused the order: {response.Body}");
        }
        else
        {
            EnsureSuccess(response, "create order");
            order = Deserialize<PretixOrder>(response.Body);
        }

        var mismatch = PriceMismatch(request, order, created);
        if (mismatch is not null)
        {
            if (created)
                await CancelOrderAsync(eventSlug, code, ct);
            throw new TicketingPlatformException($"pretix would not charge exactly the requested lines: {mismatch}");
        }

        logger.LogInformation(
            "pretix order {Code} {Outcome} for Humans order {OrderId}",
            code, created ? "created" : "found", request.OrderId);

        return new PlatformOrderSession(
            FormatPlatformOrderId(eventSlug, code), PaymentUrl(order, request.ReturnUrl), AdminOrderUrl(eventSlug, code));
    }

    public async Task<PlatformOrderStatus> GetStatusAsync(string platformOrderId, CancellationToken ct = default)
    {
        EnsureConfigured();
        var (eventSlug, code) = ParsePlatformOrderId(platformOrderId);
        var order = await SendAsync<PretixOrder>(HttpMethod.Get, OrderPath(eventSlug, code), body: null, ct);
        var status = MapStatus(order, logger);
        return status.State == PlatformOrderState.Paid && await FeeAsync(order, ct) is { } fee ? status with { FeeMinor = fee } : status;
    }

    /// <summary>
    /// What Stripe kept on the payment the status reports, read through the PaymentIntent id pretix shows in a Stripe
    /// payment's details. Null while unknown, or when the payment is not Stripe's: the reconciliation shows a fee Humans
    /// did not record.
    /// </summary>
    private async Task<long?> FeeAsync(PretixOrder order, CancellationToken ct) =>
        LastPayment(order) is { Details: { ValueKind: JsonValueKind.Object } details }
        && details.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
        && id.GetString() is { } intentId && intentId.StartsWith("pi_", StringComparison.Ordinal)
            ? await stripe.GetAccountPaymentFeeAsync(StripeAccountName, intentId, ct)
            : null;

    public async Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        EnsureConfigured();
        var (eventSlug, code) = ParsePlatformOrderId(request.PlatformOrderId);
        if (request.AmountMinor <= 0)
            return new RefundResult(RefundState.Failed, null, "A refund must be a positive amount.");

        var order = await SendAsync<PretixOrder>(HttpMethod.Get, OrderPath(eventSlug, code), body: null, ct);
        var comment = RefundCommentPrefix + request.RefundId.ToString("D");

        // A repeat answers the earlier refund's current state and never refunds again; Tickets uses it to learn
        // how a refund ended. A refund that failed after pretix accepted it gives its lowered total back.
        var fee = order.Fees.FirstOrDefault(f => !f.Canceled && string.Equals(f.InternalType, comment, StringComparison.Ordinal));
        var earlier = order.Refunds.LastOrDefault(r => r.Comment?.Contains(comment, StringComparison.Ordinal) == true);
        if (earlier is not null)
        {
            if (earlier.State is "failed" or "canceled" && fee is not null)
                await CancelFeeAsync(eventSlug, code, fee.Id, ct);
            return MapRefund(earlier, AdminOrderUrl(eventSlug, code));
        }

        var payment = RefundablePayment(order, request.AmountMinor);
        if (payment is null)
        {
            return new RefundResult(RefundState.Failed, null,
                $"No confirmed payment on pretix order {code} has {FormatMoney(request.AmountMinor)} left to refund.");
        }

        // pretix reopens a paid order for payment whenever a refund leaves its total uncovered, so the order total
        // drops by the refund first, as a negative fee that also marks the refund. The order then stays paid and
        // nobody can pay the refunded amount again.
        var feeIsNew = fee is null;
        fee ??= await AddRefundFeeAsync(eventSlug, code, comment, request.AmountMinor, ct);

        var body = new JsonObject
        {
            ["amount"] = FormatMoney(request.AmountMinor),
            ["comment"] = $"{comment} {request.Reason}".Trim(),
            ["mark_canceled"] = false,
        };
        // No answer, or a pretix 5xx, throws: Tickets keeps the refund pending and retries it under the same id, and
        // the marker on the fee and the refund makes that retry safe whether or not this one went through.
        var response = await SendRawAsync(
            HttpMethod.Post, $"{OrderPath(eventSlug, code)}payments/{payment.LocalId}/refund/", body, ct);
        if ((int)response.Status >= 500)
            EnsureSuccess(response, "refund");

        if ((int)response.Status is < 200 or > 299 && response.Status != HttpStatusCode.BadRequest)
        {
            // Refused outright (401, 403, 404): no refund was made, so the fee comes off.
            logger.LogWarning("pretix refused the refund call on {Code}: {Status} {Body}", code, (int)response.Status, response.Body);
            if (feeIsNew || StillHolds(order, request.AmountMinor))
                await CancelFeeAsync(eventSlug, code, fee.Id, ct);
            return new RefundResult(RefundState.Failed, null, $"pretix answered {(int)response.Status}: {response.Body}");
        }

        if (response.Status == HttpStatusCode.BadRequest)
        {
            logger.LogWarning("pretix declined an automatic refund on {Code}: {Body}", code, response.Body);
            if (IsManualOnly(response.Body))
            {
                // The fee stays: pretix then shows the order as overpaid by exactly this amount, which is what a
                // person refunds by hand, and the status read picks that refund up.
                return new RefundResult(RefundState.ManualRequired, null, response.Body, AdminOrderUrl(eventSlug, code));
            }

            // An older fee is left by an attempt that got no answer, or stands for a refund made by hand; it comes off
            // unless that hand refund has taken the money.
            if (feeIsNew || StillHolds(order, request.AmountMinor))
                await CancelFeeAsync(eventSlug, code, fee.Id, ct);
            return new RefundResult(RefundState.Failed, null, response.Body);
        }

        EnsureSuccess(response, "refund");
        var refund = Deserialize<PretixRefund>(response.Body);
        logger.LogInformation(
            "pretix refund {RefundLocalId} of {Amount} on {Code} for Humans refund {RefundId}: {State}",
            refund.LocalId, refund.Amount, code, request.RefundId, refund.State);
        return MapRefund(refund, AdminOrderUrl(eventSlug, code));
    }

    public async Task CancelAsync(string platformOrderId, CancellationToken ct = default)
    {
        EnsureConfigured();
        var (eventSlug, code) = ParsePlatformOrderId(platformOrderId);
        var order = await SendAsync<PretixOrder>(HttpMethod.Get, OrderPath(eventSlug, code), body: null, ct);
        if (MapStatus(order, logger).State is PlatformOrderState.Paid or PlatformOrderState.Processing)
            throw new TicketingPlatformException($"pretix order {code} is paid or its payment is settling.");
        // Expired is cancelled too: pretix still accepts a late payment on an expired order.
        if (!string.Equals(order.Status, "c", StringComparison.Ordinal))
            await CancelOrderAsync(eventSlug, code, ct);
    }

    public async Task ErasePersonalDataAsync(string platformOrderId, string email, CancellationToken ct = default)
    {
        EnsureConfigured();
        var (eventSlug, code) = ParsePlatformOrderId(platformOrderId);
        var order = await SendAsync<PretixOrder>(HttpMethod.Get, OrderPath(eventSlug, code), body: null, ct);
        var isBuyer = string.Equals(order.Email, email, StringComparison.OrdinalIgnoreCase);

        // Humans sends no names, but a person may have added some in pretix's own admin.
        foreach (var position in order.Positions.Where(p =>
                     (isBuyer || string.Equals(p.AttendeeEmail, email, StringComparison.OrdinalIgnoreCase))
                     && (p.AttendeeName is not null || p.AttendeeEmail is not null)))
        {
            await SendAsync<JsonElement>(HttpMethod.Patch, $"{EventPath(eventSlug)}orderpositions/{position.Id}/",
                new JsonObject { ["attendee_name"] = null, ["attendee_email"] = null }, ct);
        }

        if (isBuyer)
        {
            await SendAsync<JsonElement>(HttpMethod.Patch, OrderPath(eventSlug, code),
                new JsonObject { ["email"] = null, ["phone"] = null }, ct);
        }
    }

    public PlatformSetup Setup => new(
        "pretix",
        Hosting(),
        Uri.TryCreate(Settings.BaseUrl, UriKind.Absolute, out var root) && !string.IsNullOrWhiteSpace(Settings.Organizer)
            ? new Uri(root, $"control/organizer/{Uri.EscapeDataString(Settings.Organizer)}/")
            : null,
        [
            new("Pretix:BaseUrl", "Instance root: our own instance, or https://pretix.eu/ for pretix Hosted", IsSecret: false,
                !string.IsNullOrWhiteSpace(Settings.BaseUrl), NullIfBlank(Settings.BaseUrl)),
            new("Pretix:Organizer", "Organizer slug", IsSecret: false, !string.IsNullOrWhiteSpace(Settings.Organizer),
                NullIfBlank(Settings.Organizer)),
            new("PRETIX_API_TOKEN", "Team API token with order read and write on the events Humans sells", IsSecret: true,
                !string.IsNullOrWhiteSpace(Settings.ApiToken), null),
            new("PRETIX_WEBHOOK_SECRET", "The secret in the webhook URL below", IsSecret: true,
                !string.IsNullOrWhiteSpace(Settings.WebhookSecret), null),
        ],
        "/Pretix/Webhook?secret=<PRETIX_WEBHOOK_SECRET>",
        [
            $"Each event needs two active free-price items with internal names {TicketItemName} and {DonationItemName}, the returnurl plugin, Stripe with card, iDEAL and Klarna and no method fees, and buyer mails off.",
            IsHosted
                ? "pretix Hosted gives no shell for deploy/pretix/bootstrap.py: make the same settings by hand in the pretix control panel, following what bootstrap.py does."
                : "deploy/pretix/bootstrap.py, run in the pretix container, sets this up.",
            "The webhook goes on the order actions; the sales setup's event id is the pretix event slug.",
            $"Fees and payouts reconcile from the Stripe account set in pretix's Stripe settings: a Stripe:MirrorAccounts entry named {StripeAccountName} with that account's id and a read-only key (Balance, Balance transactions, Payouts, PaymentIntents, Charges, Refunds, Disputes read).",
        ]);

    private bool IsHosted => Uri.TryCreate(Settings.BaseUrl, UriKind.Absolute, out var root)
        && (root.Host.Equals("pretix.eu", StringComparison.OrdinalIgnoreCase)
            || root.Host.EndsWith(".pretix.eu", StringComparison.OrdinalIgnoreCase));

    private string Hosting() => !Uri.TryCreate(Settings.BaseUrl, UriKind.Absolute, out var root)
        ? "Not set"
        : IsHosted ? "pretix Hosted (pretix.eu)" : $"Self-hosted at {root.Host}";

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public async Task<IReadOnlyList<PlatformFinding>> CheckAsync(string? platformEventRef, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return [new(false, "Pretix:BaseUrl, Pretix:Organizer and PRETIX_API_TOKEN must all be set.")];

        var findings = new List<PlatformFinding>
        {
            string.IsNullOrWhiteSpace(Settings.WebhookSecret)
                ? new(false, "PRETIX_WEBHOOK_SECRET is unset: the webhook answers 503 and outcomes arrive only from Tickets' poll.")
                : new(true, "Webhook secret is set."),
        };
        findings.AddRange((await stripe.CheckAccountAsync(StripeAccountName, ct)).Select(c => new PlatformFinding(c.Ok, c.Text)));
        try
        {
            var events = await SendRawAsync(HttpMethod.Get,
                $"api/v1/organizers/{Uri.EscapeDataString(Settings.Organizer)}/events/?page_size=1", body: null, ct);
            findings.Add((int)events.Status is >= 200 and <= 299
                ? new(true, $"The token reads organizer {Settings.Organizer}.")
                : new(false, $"pretix answered {(int)events.Status} for organizer {Settings.Organizer}: check the token and slug."));
            if (string.IsNullOrWhiteSpace(platformEventRef) || !findings[^1].Ok)
                return findings;
            if (!EventSlug().IsMatch(platformEventRef))
            {
                findings.Add(new(false, $"'{platformEventRef}' is not a pretix event slug."));
                return findings;
            }

            var ev = await SendRawAsync(HttpMethod.Get, EventPath(platformEventRef), body: null, ct);
            if ((int)ev.Status is < 200 or > 299)
            {
                findings.Add(new(false, $"pretix answered {(int)ev.Status} for event {platformEventRef}."));
                return findings;
            }

            var details = Deserialize<PretixEvent>(ev.Body);
            findings.Add(string.Equals(details.Currency, "EUR", StringComparison.OrdinalIgnoreCase)
                ? new(true, "The event sells in EUR.")
                : new(false, $"The event sells in {details.Currency}, Humans sells in EUR."));
            findings.Add(details.Live
                ? new(true, "The shop is live.")
                : new(false, "The shop is offline, so buyers cannot reach the pay page."));
            if (details.Testmode)
                findings.Add(new(false, "The event is in pretix test mode; whether payments are real depends on the Stripe keys set in pretix."));

            var items = await SendAsync<PretixPage<PretixItem>>(
                HttpMethod.Get, $"{EventPath(platformEventRef)}items/?active=true", body: null, ct);
            foreach (var name in new[] { TicketItemName, DonationItemName })
            {
                var count = items.Results.Count(i => string.Equals(i.InternalName, name, StringComparison.Ordinal));
                findings.Add(count == 1
                    ? new(true, $"Carrier item {name} found.")
                    : new(false, $"The event has {count} active items with internal name {name}; it needs exactly one."));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            findings.Add(new(false, $"pretix could not be read: {ex.Message}"));
        }

        return findings;
    }

    public string? EventRefOf(string platformOrderId) => ParsePlatformOrderId(platformOrderId).EventSlug;

    /// <summary>
    /// The Stripe account pretix charges into; null while its mirror account is not configured. pretix puts the order
    /// code on each PaymentIntent and describes it as <c>{EVENT SLUG}-{code}</c> in capitals, so the slug is found among
    /// the organizer's events, and the Humans order is that pretix order's <c>api_meta</c>. A payment on another
    /// organizer's event, or on an order Humans did not open, names none.
    /// </summary>
    public async Task<PaymentLedger?> GetPaymentLedgerAsync(Instant from, Instant to, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return null;
        StripeAccountLedger? account;
        try
        {
            account = await stripe.GetAccountLedgerAsync(StripeAccountName, from, to, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new TicketingPlatformException(ex.Message, ex);
        }
        if (account is null)
            return null;

        Dictionary<string, string>? slugs = null;
        var orderOf = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        var entries = new List<PaymentLedgerEntry>();
        foreach (var line in account.Lines)
        {
            Guid? humansOrderId = null;
            if (line.Payment is { } payment && !orderOf.TryGetValue(payment.PaymentIntentId, out humansOrderId))
            {
                slugs ??= await EventSlugsAsync(ct);
                orderOf[payment.PaymentIntentId] = humansOrderId = await HumansOrderOfAsync(payment, slugs, ct);
            }
            entries.Add(new PaymentLedgerEntry(line.Id, line.CreatedAt, KindOf(line.ReportingCategory),
                line.AmountMinor, line.FeeMinor, humansOrderId, line.Description));
        }

        return new PaymentLedger(entries, account.Payouts.Select(p => new PaymentPayout(
            p.Id, p.CreatedAt, p.ArrivalAt, p.AmountMinor, p.Status, p.ContentsNetMinor, p.ContentsCount)).ToList());
    }

    private async Task<Guid?> HumansOrderOfAsync(
        StripeLedgerPayment payment, Dictionary<string, string> slugs, CancellationToken ct)
    {
        if (!payment.Metadata.TryGetValue("code", out var code) || string.IsNullOrEmpty(code)
            || payment.Description is not { } description || !description.EndsWith($"-{code}", StringComparison.Ordinal)
            || !slugs.TryGetValue(description[..^(code.Length + 1)], out var slug))
            return null;
        var response = await SendRawAsync(HttpMethod.Get, OrderPath(slug, code), body: null, ct);
        if (response.Status == HttpStatusCode.NotFound)
            return null;
        EnsureSuccess(response, "read order");
        return HumansOrderId(Deserialize<PretixOrder>(response.Body));
    }

    /// <summary>By Stripe's reporting category, as Box Office reads its own account.</summary>
    private static PaymentLedgerEntryKind KindOf(string reportingCategory) => reportingCategory switch
    {
        "charge" or "charge_failure" or "partial_capture_reversal" => PaymentLedgerEntryKind.Charge,
        "refund" or "refund_failure" => PaymentLedgerEntryKind.Refund,
        "dispute" or "dispute_reversal" => PaymentLedgerEntryKind.Chargeback,
        "fee" => PaymentLedgerEntryKind.Fee,
        "payout" or "payout_reversal" => PaymentLedgerEntryKind.Payout,
        _ => PaymentLedgerEntryKind.Other,
    };

    /// <summary>The organizer's event slugs, by the capitalised form pretix writes on a payment.</summary>
    private async Task<Dictionary<string, string>> EventSlugsAsync(CancellationToken ct)
    {
        var slugs = new Dictionary<string, string>(StringComparer.Ordinal);
        for (string? next = $"api/v1/organizers/{Uri.EscapeDataString(Settings.Organizer)}/events/"; next is not null;)
        {
            var page = await SendAsync<PretixPage<PretixEventSlug>>(HttpMethod.Get, next, body: null, ct);
            foreach (var ev in page.Results)
                slugs[ev.Slug.ToUpperInvariant()] = ev.Slug;
            next = page.Next;
        }

        return slugs;
    }

    private static Guid? HumansOrderId(PretixOrder order) =>
        order.ApiMeta?.TryGetValue(HumansOrderIdMeta, out var meta) == true
        && meta.ValueKind == JsonValueKind.String && Guid.TryParse(meta.GetString(), out var orderId)
            ? orderId
            : null;

    public async Task<IReadOnlyList<PlatformOrderSummary>> ListOrdersAsync(
        string? platformEventRef, Instant from, Instant to, CancellationToken ct = default)
    {
        EnsureConfigured();
        if (string.IsNullOrWhiteSpace(platformEventRef))
            throw new TicketingPlatformException("pretix needs the event's slug to list its orders.");

        // Inactive carriers too: an order keeps its item after the item is switched off.
        var ticketItems = new HashSet<int>();
        for (string? next = $"{EventPath(platformEventRef)}items/"; next is not null;)
        {
            var page = await SendAsync<PretixPage<PretixItem>>(HttpMethod.Get, next, body: null, ct);
            ticketItems.UnionWith(page.Results
                .Where(i => string.Equals(i.InternalName, TicketItemName, StringComparison.Ordinal)).Select(i => i.Id));
            next = page.Next;
        }

        var orders = new List<PlatformOrderSummary>();
        for (string? next = $"{EventPath(platformEventRef)}orders/?created_since={Uri.EscapeDataString(InstantPattern.ExtendedIso.Format(from))}"
                 + $"&created_before={Uri.EscapeDataString(InstantPattern.ExtendedIso.Format(to))}&ordering=datetime";
             next is not null;)
        {
            var page = await SendAsync<PretixPage<PretixOrder>>(HttpMethod.Get, next, body: null, ct);
            orders.AddRange(page.Results.Select(order => Summarize(platformEventRef, order, ticketItems)));
            next = page.Next;
        }

        return orders;
    }

    private PlatformOrderSummary Summarize(string eventSlug, PretixOrder order, IReadOnlySet<int> ticketItems)
    {
        var status = MapStatus(order, logger);
        // The adapter's refund fees lower the total by what was refunded; the refund itself is counted on its own.
        var refundFees = order.Fees
            .Where(f => !f.Canceled && f.InternalType?.StartsWith(RefundCommentPrefix, StringComparison.Ordinal) == true)
            .Sum(f => f.Value);
        var received = Received(order);
        // Tickets never takes a paid order back, but pretix cancels or reopens one once a person processes a refund or
        // chargeback, and keeps a late-paid one expired. For the list, an order that took its money stays paid,
        // whatever went back afterwards; GetStatusAsync keeps the net rule because it decides whether tickets issue.
        var state = status.State != PlatformOrderState.Paid && received > 0 && received >= order.Total - refundFees
            ? PlatformOrderState.Paid
            : status.State;
        // A paid order is charged what pretix received, so one paid twice or short shows as a total difference.
        var charged = state == PlatformOrderState.Paid ? received : order.Total - refundFees;
        return new PlatformOrderSummary(
            FormatPlatformOrderId(eventSlug, order.Code),
            HumansOrderId(order),
            ParseInstant(order.Datetime) ?? throw new HttpRequestException($"pretix order {order.Code} has no datetime."),
            state,
            ToMinor(charged),
            order.Positions.Count(p => !p.Canceled && ticketItems.Contains(p.Item)),
            status.RefundedMinor,
            status.ChargedBackMinor);
    }

    internal static string OrderCode(Guid orderId)
    {
        // Hashed, not the Guid's own bytes: a version 7 Guid starts with a timestamp, which would make the codes of
        // orders created in the same millisecond collide.
        var hash = SHA256.HashData(orderId.ToByteArray());
        var code = new StringBuilder(CodeLength);
        for (var i = 0; i < CodeLength; i++)
        {
            var bit = i * 5;
            var value = ((hash[bit / 8] << 8) | hash[(bit / 8) + 1]) >> (11 - (bit % 8)) & 0x1F;
            code.Append(CodeAlphabet[value]);
        }

        return code.ToString();
    }

    internal static string FormatPlatformOrderId(string eventSlug, string code) => $"{eventSlug}/{code}";

    internal static (string EventSlug, string Code) ParsePlatformOrderId(string platformOrderId)
    {
        var parts = platformOrderId.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"Not a pretix order id: '{platformOrderId}'.", nameof(platformOrderId));
        return (parts[0], parts[1]);
    }

    internal static PlatformOrderStatus MapStatus(PretixOrder order, ILogger logger)
    {
        var state = order.Status switch
        {
            "n" => PlatformOrderState.Pending,
            "p" => PlatformOrderState.Paid,
            "e" => PlatformOrderState.Expired,
            "c" => PlatformOrderState.Cancelled,
            _ => throw new HttpRequestException($"pretix order {order.Code} has unknown status '{order.Status}'."),
        };

        if (IsCovered(order, state))
            state = PlatformOrderState.Paid;
        // A "pending" payment has left the buyer (a SEPA debit settling); a bank transfer not yet sent stays "created".
        else if (state == PlatformOrderState.Pending && order.Payments.Any(p => string.Equals(p.State, "pending", StringComparison.Ordinal)))
            state = PlatformOrderState.Processing;

        // The buyer pays exactly the lines, so bootstrap.py sets every method's fee to zero. A surcharge here is
        // misconfiguration; it is logged rather than thrown, because a throw would hide a real payment from Humans.
        if (order.Fees.Any(f => string.Equals(f.FeeType, "payment", StringComparison.Ordinal) && !f.Canceled))
            logger.LogError("pretix order {Code} carries a payment surcharge; set the payment method fees to zero", order.Code);

        // A full refund drops the total to zero and pretix confirms a 0.00 "free" payment; that is not how they paid.
        var lastPayment = LastPayment(order);
        // A refund made outside pretix (Stripe dashboard, chargeback) waits as "external" until a person processes it,
        // but the money has already gone, so it counts from the moment pretix records it.
        var gone = order.Refunds.Where(r => r.State is "done" or "external").ToList();
        var disputed = gone.Where(IsChargeback).Sum(r => ToMinor(r.Amount));
        var refunded = gone.Where(r => !IsChargeback(r)).Sum(r => ToMinor(r.Amount));

        return new PlatformOrderStatus(
            state,
            // pretix does not know what Stripe kept.
            null,
            lastPayment?.Provider,
            // The order's own payment_date is a date only; the confirmed payment carries the time.
            state == PlatformOrderState.Paid ? ParseInstant(lastPayment?.PaymentDate) : null,
            refunded,
            // Only the newest refund counts: a retry after a failure must not read as failed while its money moves.
            // An external one cancelled in pretix is a won chargeback or a mistaken record, not a failed refund.
            order.Refunds.Where(r => !string.Equals(r.Source, "external", StringComparison.Ordinal))
                .MaxBy(r => r.LocalId)?.State is "failed" or "canceled",
            disputed);
    }

    private static PretixPayment? LastPayment(PretixOrder order) =>
        order.Payments.LastOrDefault(p => string.Equals(p.State, "confirmed", StringComparison.Ordinal) && p.Amount > 0)
        ?? order.Payments.LastOrDefault(p => string.Equals(p.State, "refunded", StringComparison.Ordinal));

    /// <summary>
    /// pretix's Stripe plugin records a chargeback as an external refund carrying the Stripe dispute's id, and never
    /// updates it. A won dispute or an inquiry clears only when a person cancels that refund in pretix, which pretix
    /// allows only while it is still <c>external</c>; once processed (or completed by pretix itself) it stays disputed.
    /// </summary>
    private static bool IsChargeback(PretixRefund refund) =>
        string.Equals(refund.Source, "external", StringComparison.Ordinal)
        && refund.Details is { ValueKind: JsonValueKind.Object } details
        && details.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
        && id.GetString() is { } stripeId
        && (stripeId.StartsWith("dp_", StringComparison.Ordinal) || stripeId.StartsWith("du_", StringComparison.Ordinal));

    /// <summary>The order still holds <paramref name="amountMinor"/> beyond its total: no refund has paid it out.</summary>
    private static bool StillHolds(PretixOrder order, long amountMinor) =>
        ToMinor(NetPaid(order) - order.Total) >= amountMinor;

    /// <summary>What the buyer paid in: a payment refunded since still came in.</summary>
    private static decimal Received(PretixOrder order) =>
        order.Payments.Where(p => p.State is "confirmed" or "refunded").Sum(p => p.Amount);

    private static decimal NetPaid(PretixOrder order) =>
        Received(order)
        - order.Refunds.Where(r => r.State is "done" or "transit" or "created" or "external").Sum(r => r.Amount);

    /// <summary>
    /// pretix keeps a payment confirmed after expiry or cancellation but cannot mark the order paid, and a refund made
    /// outside this adapter can reopen a paid order. The money decides, so a late payment is reported, never stranded.
    /// </summary>
    private static bool IsCovered(PretixOrder order, PlatformOrderState state) => state switch
    {
        PlatformOrderState.Pending or PlatformOrderState.Expired => Received(order) > 0 && NetPaid(order) >= order.Total,
        PlatformOrderState.Cancelled => NetPaid(order) > 0,
        _ => false,
    };

    private static int CarrierItem(PretixPage<PretixItem> items, string internalName) =>
        items.Results.SingleOrDefault(i => string.Equals(i.InternalName, internalName, StringComparison.Ordinal))?.Id
        ?? throw new TicketingPlatformException(
            $"The pretix event has no active item with internal name '{internalName}' (see deploy/pretix/bootstrap.py).");

    private static JsonObject BuildOrderBody(PlatformOrderRequest request, string code, int ticketItem, int donationItem)
    {
        var positions = new JsonArray();
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            var position = new JsonObject
            {
                ["positionid"] = i + 1,
                ["item"] = line.Kind == OrderLineKind.Ticket ? ticketItem : donationItem,
                ["price"] = FormatMoney(line.AmountMinor),
            };
            positions.Add(position);
        }

        return new JsonObject
        {
            ["code"] = code,
            ["email"] = request.BuyerEmail,
            ["locale"] = CultureInfo.GetCultureInfo(request.Culture).TwoLetterISOLanguageName,
            ["sales_channel"] = "web",
            ["expires"] = InstantPattern.ExtendedIso.Format(request.ExpiresAt),
            // Humans owns capacity, and pretix's own order mails would duplicate Humans' ticket mail.
            ["force"] = true,
            ["send_email"] = false,
            // No names: pretix needs only the email to take a payment, and Humans keeps the rest.
            ["comment"] = $"Humans order {request.OrderId:D}",
            ["api_meta"] = new JsonObject { [HumansOrderIdMeta] = request.OrderId.ToString("D") },
            ["positions"] = positions,
        };
    }

    private static void Validate(PlatformOrderRequest request)
    {
        if (request.Lines.Count == 0)
            throw new TicketingPlatformException("A checkout needs at least one line.");
        if (request.Lines.Any(l => l.AmountMinor < 0))
            throw new TicketingPlatformException("pretix cannot charge a negative line.");
    }

    /// <summary>
    /// pretix believes the API's prices but taxes each position by its item's tax rule, and a rule set to add tax on
    /// top would raise the total. So the order is checked against the request line by line, and a fresh order's total
    /// too (a retried one may already carry the buyer's payment fee).
    /// </summary>
    private static string? PriceMismatch(PlatformOrderRequest request, PretixOrder order, bool fresh)
    {
        var positions = order.Positions.Where(p => !p.Canceled).OrderBy(p => p.Positionid).ToList();
        if (positions.Count != request.Lines.Count)
            return $"{positions.Count} positions for {request.Lines.Count} lines";

        for (var i = 0; i < positions.Count; i++)
        {
            var line = request.Lines[i];
            var position = positions[i];
            if (ToMinor(position.Price) != line.AmountMinor)
                return $"line {i + 1} costs {position.Price}, not {FormatMoney(line.AmountMinor)}";
            if (position.TaxRate != line.VatRate * 100m)
                return $"line {i + 1} is taxed at {position.TaxRate}%, not {line.VatRate * 100m}%";
        }

        var expected = request.Lines.Sum(l => l.AmountMinor);
        return fresh && ToMinor(order.Total) != expected
            ? $"total {order.Total}, not {FormatMoney(expected)}"
            : null;
    }

    private Uri PaymentUrl(PretixOrder order, Uri returnUrl)
    {
        if (!string.Equals(order.Status, "n", StringComparison.Ordinal))
            return new Uri(order.Url);

        // Visiting any order sub-page with return_url makes the returnurl plugin send the buyer back once the order
        // page is reached again, which pretix does after every payment attempt. The URL must match the plugin's
        // prefix list in the event settings.
        return new Uri($"{order.Url}pay/change?return_url={Uri.EscapeDataString(returnUrl.AbsoluteUri)}");
    }

    private static PretixPayment? RefundablePayment(PretixOrder order, long amountMinor) =>
        order.Payments
            .Where(p => string.Equals(p.State, "confirmed", StringComparison.Ordinal))
            .Select(p => (Payment: p, Left: ToMinor(p.Amount) - order.Refunds
                .Where(r => r.Payment == p.LocalId && r.State is not ("failed" or "canceled"))
                .Sum(r => ToMinor(r.Amount))))
            .Where(x => x.Left >= amountMinor)
            .Select(x => x.Payment)
            .LastOrDefault();

    private static RefundResult MapRefund(PretixRefund refund, Uri manualUrl) => refund.State switch
    {
        "done" => new RefundResult(RefundState.Succeeded, refund.LocalId.ToString(CultureInfo.InvariantCulture), null),
        // A manual-provider refund is created and then waits for a person to send the money and mark it done.
        "created" when string.Equals(refund.Provider, "manual", StringComparison.Ordinal) => new RefundResult(
            RefundState.ManualRequired, refund.LocalId.ToString(CultureInfo.InvariantCulture), null, manualUrl),
        "created" or "transit" or "external" => new RefundResult(
            RefundState.Pending, refund.LocalId.ToString(CultureInfo.InvariantCulture), null),
        _ => new RefundResult(
            RefundState.Failed, refund.LocalId.ToString(CultureInfo.InvariantCulture), $"pretix refund {refund.State}"),
    };

    /// <summary>pretix's 400 for a provider that cannot refund this amount itself; every other 400 is a decline.</summary>
    internal static bool IsManualOnly(string body) =>
        body.Contains("refund not available for this payment method", StringComparison.Ordinal);

    private async Task<PretixFee> AddRefundFeeAsync(
        string eventSlug, string code, string marker, long amountMinor, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["send_email"] = false,
            ["reissue_invoice"] = false,
            ["create_fees"] = new JsonArray(new JsonObject
            {
                ["fee_type"] = "other",
                ["value"] = "-" + FormatMoney(amountMinor),
                ["description"] = "Refund",
                ["internal_type"] = marker,
            }),
        };
        var order = await SendAsync<PretixOrder>(HttpMethod.Post, $"{OrderPath(eventSlug, code)}change/", body, ct);
        return order.Fees.First(f => !f.Canceled && string.Equals(f.InternalType, marker, StringComparison.Ordinal));
    }

    private async Task CancelFeeAsync(string eventSlug, string code, int feeId, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["send_email"] = false,
            ["reissue_invoice"] = false,
            ["cancel_fees"] = new JsonArray(new JsonObject { ["fee"] = feeId }),
        };
        var response = await SendRawAsync(HttpMethod.Post, $"{OrderPath(eventSlug, code)}change/", body, ct);
        if ((int)response.Status is < 200 or > 299)
            logger.LogError("Could not take refund fee {FeeId} off pretix order {Code}: {Body}", feeId, code, response.Body);
    }

    private Uri AdminOrderUrl(string eventSlug, string code) =>
        new(new Uri(Settings.BaseUrl), $"control/event/{Settings.Organizer}/{eventSlug}/orders/{code}/");

    private async Task CancelOrderAsync(string eventSlug, string code, CancellationToken ct)
    {
        var response = await SendRawAsync(
            HttpMethod.Post, $"{OrderPath(eventSlug, code)}mark_canceled/", new JsonObject { ["send_email"] = false }, ct);
        EnsureSuccess(response, "cancel order");
    }

    private Task<RawResponse> PostOrderAsync(string eventSlug, JsonObject body, CancellationToken ct) =>
        SendRawAsync(HttpMethod.Post, $"{EventPath(eventSlug)}orders/", body, ct);

    private string EventPath(string eventSlug) =>
        $"api/v1/organizers/{Uri.EscapeDataString(Settings.Organizer)}/events/{Uri.EscapeDataString(eventSlug)}/";

    private string OrderPath(string eventSlug, string code) =>
        $"{EventPath(eventSlug)}orders/{Uri.EscapeDataString(code)}/";

    private async Task<T> SendAsync<T>(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        var response = await SendRawAsync(method, path, body, ct);
        EnsureSuccess(response, $"{method} {path}");
        return Deserialize<T>(response.Body);
    }

    private async Task<RawResponse> SendRawAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(method, new Uri(new Uri(Settings.BaseUrl), path));
        message.Headers.Authorization = new AuthenticationHeaderValue("Token", Settings.ApiToken);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
            message.Content = JsonContent.Create(body);

        using var response = await http.SendAsync(message, ct);
        return new RawResponse(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private static void EnsureSuccess(RawResponse response, string what)
    {
        if ((int)response.Status is < 200 or > 299)
            throw new HttpRequestException(
                $"pretix {what} failed with {(int)response.Status}: {response.Body}", null, response.Status);
    }

    private static T Deserialize<T>(string body) =>
        JsonSerializer.Deserialize<T>(body, Json)
        ?? throw new HttpRequestException($"pretix returned an empty {typeof(T).Name}.");

    private static bool HasError(string body, string field)
    {
        try
        {
            return JsonNode.Parse(body) is JsonObject errors && errors.ContainsKey(field);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new TicketingPlatformException("pretix is not configured.");
    }

    private static string FormatMoney(long minor) =>
        (minor / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    private static long ToMinor(decimal amount) => (long)decimal.Round(amount * 100m, MidpointRounding.AwayFromZero);

    private static Instant? ParseInstant(string? value)
    {
        if (value is null) return null;
        var parsed = OffsetDateTimePattern.ExtendedIso.Parse(value);
        return parsed.Success
            ? parsed.Value.ToInstant()
            : throw new HttpRequestException($"pretix returned an unreadable date '{value}'.");
    }

    private sealed record RawResponse(HttpStatusCode Status, string Body);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex EventSlug();

    private sealed record PretixEvent(string Currency, bool Live, bool Testmode);

    internal sealed record PretixPage<T>(IReadOnlyList<T> Results, string? Next = null);

    internal sealed record PretixItem(int Id, string? InternalName);

    internal sealed record PretixOrder(
        string Code,
        string Status,
        string? Email,
        string Url,
        decimal Total,
        string? Datetime,
        IReadOnlyList<PretixPosition> Positions,
        IReadOnlyList<PretixFee> Fees,
        IReadOnlyList<PretixPayment> Payments,
        IReadOnlyList<PretixRefund> Refunds,
        IReadOnlyDictionary<string, JsonElement>? ApiMeta);

    internal sealed record PretixPosition(
        int Id, int Positionid, decimal Price, decimal TaxRate, bool Canceled, string? AttendeeName, string? AttendeeEmail,
        int Item = 0);

    internal sealed record PretixFee(int Id, string FeeType, decimal Value, bool Canceled, string? InternalType);

    internal sealed record PretixPayment(
        int LocalId, string State, decimal Amount, string Provider, string? PaymentDate, JsonElement? Details = null);

    private sealed record PretixEventSlug(string Slug);

    internal sealed record PretixRefund(
        int LocalId, string State, decimal Amount, int? Payment, string? Comment, string? Provider,
        string? Source = null, JsonElement? Details = null);
}
