using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace Humans.Pretix.Services;

/// <summary>
/// <para>
/// Fills Tickets' checkout port with stock pretix (2026.9): an order created through the REST API at Humans' prices,
/// paid by the buyer on pretix's own order page, which sends them back to <see cref="CheckoutRequest.ReturnUrl"/>
/// through pretix's bundled <c>returnurl</c> plugin.
/// </para>
/// <para>
/// The backend order id is <c>{event slug}/{order code}</c>, because pretix addresses an order by both and the port
/// passes only the id. The code is derived from <see cref="CheckoutRequest.OrderId"/>, so a retried create finds the
/// order it already made instead of making a second one.
/// </para>
/// </summary>
internal sealed class PretixCheckoutBackend(
    HttpClient http,
    IOptions<PretixSettings> options,
    ILogger<PretixCheckoutBackend> logger) : ITicketCheckoutBackend
{
    public const string BackendKey = "pretix";

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

    public string Key => BackendKey;

    public bool IsConfigured => Settings.IsConfigured;

    public async Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct = default)
    {
        EnsureConfigured();
        var eventSlug = request.BackendEventRef;
        if (string.IsNullOrWhiteSpace(eventSlug))
            throw new TicketCheckoutException("pretix needs the event's slug as BackendEventRef.");
        Validate(request);

        var ev = await SendAsync<PretixEvent>(HttpMethod.Get, EventPath(eventSlug), body: null, ct);
        if (!string.Equals(ev.Currency, request.Currency, StringComparison.OrdinalIgnoreCase))
            throw new TicketCheckoutException(
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
                throw new TicketCheckoutException(
                    $"pretix order code {code} already belongs to another order.");
            }

            created = false;
        }
        else if (response.Status == HttpStatusCode.BadRequest)
        {
            throw new TicketCheckoutException($"pretix refused the order: {response.Body}");
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
            throw new TicketCheckoutException($"pretix would not charge exactly the requested lines: {mismatch}");
        }

        logger.LogInformation(
            "pretix order {Code} {Outcome} for Humans order {OrderId}",
            code, created ? "created" : "found", request.OrderId);

        return new CheckoutSession(
            FormatBackendOrderId(eventSlug, code), PaymentUrl(order, request.ReturnUrl), AdminOrderUrl(eventSlug, code));
    }

    public async Task<CheckoutStatus> GetStatusAsync(string backendOrderId, CancellationToken ct = default)
    {
        EnsureConfigured();
        var (eventSlug, code) = ParseBackendOrderId(backendOrderId);
        var order = await SendAsync<PretixOrder>(HttpMethod.Get, OrderPath(eventSlug, code), body: null, ct);
        return MapStatus(order);
    }

    public async Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        EnsureConfigured();
        var (eventSlug, code) = ParseBackendOrderId(request.BackendOrderId);
        if (request.AmountMinor <= 0)
            return new RefundResult(RefundState.Failed, null, "A refund must be a positive amount.");

        var order = await SendAsync<PretixOrder>(HttpMethod.Get, OrderPath(eventSlug, code), body: null, ct);
        var comment = RefundCommentPrefix + request.RefundId.ToString("D");

        var earlier = order.Refunds.FirstOrDefault(r =>
            r.Comment?.Contains(comment, StringComparison.Ordinal) == true
            && r.State is not ("failed" or "canceled"));
        if (earlier is not null)
            return MapRefund(earlier, AdminOrderUrl(eventSlug, code));

        var payment = RefundablePayment(order, request.AmountMinor);
        if (payment is null)
        {
            return new RefundResult(RefundState.Failed, null,
                $"No confirmed payment on pretix order {code} has {FormatMoney(request.AmountMinor)} left to refund.");
        }

        var body = new JsonObject
        {
            ["amount"] = FormatMoney(request.AmountMinor),
            ["comment"] = $"{comment} {request.Reason}".Trim(),
            ["mark_canceled"] = false,
        };
        var response = await SendRawAsync(
            HttpMethod.Post, $"{OrderPath(eventSlug, code)}payments/{payment.LocalId}/refund/", body, ct);

        if (response.Status == HttpStatusCode.BadRequest)
        {
            // Typically the payment method cannot refund automatically (bank transfer, or a partial refund the
            // provider does not support). A person issues it in pretix; the status read picks it up.
            logger.LogWarning(
                "pretix declined an automatic refund on {Code}: {Body}", code, response.Body);
            return new RefundResult(RefundState.ManualRequired, null, response.Body, AdminOrderUrl(eventSlug, code));
        }

        EnsureSuccess(response, "refund");
        var refund = Deserialize<PretixRefund>(response.Body);
        logger.LogInformation(
            "pretix refund {RefundLocalId} of {Amount} on {Code} for Humans refund {RefundId}: {State}",
            refund.LocalId, refund.Amount, code, request.RefundId, refund.State);
        return MapRefund(refund, AdminOrderUrl(eventSlug, code));
    }

    public async Task CancelAsync(string backendOrderId, CancellationToken ct = default)
    {
        EnsureConfigured();
        var (eventSlug, code) = ParseBackendOrderId(backendOrderId);
        var order = await SendAsync<PretixOrder>(HttpMethod.Get, OrderPath(eventSlug, code), body: null, ct);
        switch (order.Status)
        {
            case "p":
                throw new TicketCheckoutException($"pretix order {code} is already paid.");
            case "c":
                return;
            default:
                // Expired is cancelled too: pretix still accepts a late payment on an expired order.
                await CancelOrderAsync(eventSlug, code, ct);
                return;
        }
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

    internal static string FormatBackendOrderId(string eventSlug, string code) => $"{eventSlug}/{code}";

    internal static (string EventSlug, string Code) ParseBackendOrderId(string backendOrderId)
    {
        var parts = backendOrderId.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"Not a pretix order id: '{backendOrderId}'.", nameof(backendOrderId));
        return (parts[0], parts[1]);
    }

    internal static CheckoutStatus MapStatus(PretixOrder order)
    {
        var state = order.Status switch
        {
            "n" => CheckoutState.Pending,
            "p" => CheckoutState.Paid,
            "e" => CheckoutState.Expired,
            "c" => CheckoutState.Cancelled,
            _ => throw new HttpRequestException($"pretix order {order.Code} has unknown status '{order.Status}'."),
        };

        var paymentFees = order.Fees.Where(f => string.Equals(f.FeeType, "payment", StringComparison.Ordinal) && !f.Canceled).ToList();
        var lastPayment = order.Payments.LastOrDefault(p => string.Equals(p.State, "confirmed", StringComparison.Ordinal));
        var refunded = order.Refunds.Where(r => string.Equals(r.State, "done", StringComparison.Ordinal)).Sum(r => ToMinor(r.Amount));

        return new CheckoutStatus(
            state,
            paymentFees.Count == 0 ? null : paymentFees.Sum(f => ToMinor(f.Value)),
            lastPayment?.Provider,
            state == CheckoutState.Paid ? ParseInstant(order.PaymentDate) : null,
            refunded);
    }

    private static int CarrierItem(PretixPage<PretixItem> items, string internalName) =>
        items.Results.SingleOrDefault(i => string.Equals(i.InternalName, internalName, StringComparison.Ordinal))?.Id
        ?? throw new TicketCheckoutException(
            $"The pretix event has no active item with internal name '{internalName}' (see deploy/pretix/bootstrap.py).");

    private static JsonObject BuildOrderBody(CheckoutRequest request, string code, int ticketItem, int donationItem)
    {
        var positions = new JsonArray();
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            var position = new JsonObject
            {
                ["positionid"] = i + 1,
                ["item"] = line.Kind == CheckoutLineKind.Ticket ? ticketItem : donationItem,
                ["price"] = FormatMoney(line.AmountMinor),
            };
            if (line.Kind == CheckoutLineKind.Ticket)
                position["attendee_name"] = line.AttendeeName;
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
            ["comment"] = $"Humans order {request.OrderId:D}, paid by {request.BuyerName}",
            ["api_meta"] = new JsonObject { [HumansOrderIdMeta] = request.OrderId.ToString("D") },
            ["positions"] = positions,
        };
    }

    private static void Validate(CheckoutRequest request)
    {
        if (request.Lines.Count == 0)
            throw new TicketCheckoutException("A checkout needs at least one line.");
        if (request.Lines.Any(l => l.AmountMinor < 0))
            throw new TicketCheckoutException("pretix cannot charge a negative line.");
        if (request.Lines.Any(l => l.Kind == CheckoutLineKind.Ticket && string.IsNullOrWhiteSpace(l.AttendeeName)))
            throw new TicketCheckoutException("Every ticket line needs the attendee's name.");
    }

    /// <summary>
    /// pretix believes the API's prices but taxes each position by its item's tax rule, and a rule set to add tax on
    /// top would raise the total. So the order is checked against the request line by line, and a fresh order's total
    /// too (a retried one may already carry the buyer's payment fee).
    /// </summary>
    private static string? PriceMismatch(CheckoutRequest request, PretixOrder order, bool fresh)
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
            throw new TicketCheckoutException("pretix is not configured.");
    }

    private static string FormatMoney(long minor) =>
        (minor / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    private static long ToMinor(decimal amount) => (long)decimal.Round(amount * 100m, MidpointRounding.AwayFromZero);

    private static Instant? ParseInstant(string? value) =>
        value is null ? null : Instant.FromDateTimeOffset(DateTimeOffset.Parse(value, CultureInfo.InvariantCulture));

    private sealed record RawResponse(HttpStatusCode Status, string Body);

    internal sealed record PretixEvent(string Currency);

    internal sealed record PretixPage<T>(IReadOnlyList<T> Results);

    internal sealed record PretixItem(int Id, string? InternalName);

    internal sealed record PretixOrder(
        string Code,
        string Status,
        string Url,
        decimal Total,
        string? PaymentDate,
        IReadOnlyList<PretixPosition> Positions,
        IReadOnlyList<PretixFee> Fees,
        IReadOnlyList<PretixPayment> Payments,
        IReadOnlyList<PretixRefund> Refunds,
        IReadOnlyDictionary<string, JsonElement>? ApiMeta);

    internal sealed record PretixPosition(int Positionid, decimal Price, decimal TaxRate, bool Canceled);

    internal sealed record PretixFee(string FeeType, decimal Value, bool Canceled);

    internal sealed record PretixPayment(int LocalId, string State, decimal Amount, string Provider);

    internal sealed record PretixRefund(
        int LocalId, string State, decimal Amount, int? Payment, string? Comment, string? Provider);
}
