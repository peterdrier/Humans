using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace Humans.HiEvents.Services;

/// <summary>
/// <para>
/// Talks to the Nobodies fork of Hi.Events, which adds <c>POST /events/{id}/orders/checkout</c>: a reserved order at
/// caller-set prices with buyer and attendee names filled in, returning a payment link, idempotent by
/// <c>external_reference</c> (the Humans order id). Refunds pass the Humans refund id as <c>idempotency_key</c>.
/// </para>
/// <para>
/// A backend order id is <c>{eventId}:{orderId}</c>, because every Hi.Events order route is scoped to its event.
/// Transport and unexpected HTTP failures throw <see cref="HttpRequestException"/>.
/// </para>
/// </summary>
internal sealed class HiEventsCheckoutBackend : ITicketCheckoutBackend
{
    public const string BackendKey = "hievents";

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
    private readonly ILogger<HiEventsCheckoutBackend> _logger;

    public HiEventsCheckoutBackend(
        HttpClient http,
        IOptions<HiEventsSettings> settings,
        IClock clock,
        ILogger<HiEventsCheckoutBackend> logger)
    {
        _http = http;
        _settings = settings.Value;
        _clock = clock;
        _logger = logger;

        if (!string.IsNullOrEmpty(_settings.ApiUrl))
        {
            _http.BaseAddress = new Uri(_settings.ApiUrl.TrimEnd('/') + "/");
        }

        if (!string.IsNullOrEmpty(_settings.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        }

        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string Key => BackendKey;

    public bool IsConfigured => _settings.IsConfigured;

    public async Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct = default)
    {
        if (!int.TryParse(request.BackendEventRef, NumberStyles.None, CultureInfo.InvariantCulture, out var eventId))
            throw new TicketCheckoutException($"Hi.Events needs a numeric event id, got '{request.BackendEventRef}'.");

        var body = new CheckoutOrderBody(
            ExternalReference: request.OrderId.ToString("N"),
            FirstName: request.BuyerName,
            LastName: null,
            Email: request.BuyerEmail,
            Locale: ToLocale(request.Culture),
            ExpiresAt: InstantPattern.ExtendedIso.Format(request.ExpiresAt),
            Items: request.Lines.Select(ToItem).ToList());

        using var response = await _http.PostAsJsonAsync($"events/{eventId}/orders/checkout", body, JsonOptions, ct);

        if (response.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict)
        {
            throw new TicketCheckoutException(
                $"Hi.Events refused the checkout for order {request.OrderId}: {await response.Content.ReadAsStringAsync(ct)}");
        }

        var order = await ReadOrderAsync(response, ct);

        if (!string.Equals(order.Currency, request.Currency, StringComparison.OrdinalIgnoreCase))
        {
            throw new TicketCheckoutException(
                $"Hi.Events event {eventId} sells in {order.Currency}, not {request.Currency}.");
        }

        if (ToMinor(order.TotalGross) != request.Lines.Sum(l => l.AmountMinor))
        {
            throw new TicketCheckoutException(
                $"Hi.Events order {order.Id} totals {order.TotalGross}, not the requested lines.");
        }

        var paymentUrl = order.PaymentLinkUrl
            ?? throw new HttpRequestException($"Hi.Events order {order.Id} came back without a payment link.");

        return new CheckoutSession(ToBackendOrderId(eventId, order.Id), new Uri(paymentUrl));
    }

    public async Task<CheckoutStatus> GetStatusAsync(string backendOrderId, CancellationToken ct = default)
    {
        var (eventId, orderId) = ParseBackendOrderId(backendOrderId);

        using var response = await _http.GetAsync($"events/{eventId}/orders/{orderId}", ct);
        var order = await ReadOrderAsync(response, ct);

        return new CheckoutStatus(
            State: ToState(order),
            FeeMinor: null,
            PaymentMethod: order.PaymentProvider?.ToLowerInvariant(),
            PaidAt: null,
            RefundedMinor: ToMinor(order.TotalRefunded ?? 0m));
    }

    public async Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        var (eventId, orderId) = ParseBackendOrderId(request.BackendOrderId);

        var body = new RefundBody(
            Amount: request.AmountMinor / 100m,
            NotifyBuyer: true,
            CancelOrder: false,
            IdempotencyKey: request.RefundId.ToString("N"));

        using var response = await _http.PostAsJsonAsync($"events/{eventId}/orders/{orderId}/refund", body, JsonOptions, ct);

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var reason = await response.Content.ReadAsStringAsync(ct);
            var current = await GetOrderAsync(eventId, orderId, ct);

            // A refund already in flight is most likely this one, retried after a lost response; the order status
            // shows the outcome either way.
            if (string.Equals(current.RefundStatus, "REFUND_PENDING", StringComparison.Ordinal))
                return new RefundResult(RefundState.Pending, null, null);

            _logger.LogWarning("Hi.Events declined refund {RefundId} on {BackendOrderId}: {Reason}",
                request.RefundId, request.BackendOrderId, reason);
            return new RefundResult(RefundState.Failed, null, reason);
        }

        await ReadOrderAsync(response, ct);

        // Hi.Events refunds through Stripe asynchronously; the amount shows in RefundedMinor once Stripe confirms.
        return new RefundResult(RefundState.Pending, null, null);
    }

    public async Task CancelAsync(string backendOrderId, CancellationToken ct = default)
    {
        var (eventId, orderId) = ParseBackendOrderId(backendOrderId);
        var order = await GetOrderAsync(eventId, orderId, ct);

        switch (ToState(order))
        {
            case CheckoutState.Paid:
                throw new TicketCheckoutException($"Hi.Events order {backendOrderId} is paid and cannot be cancelled.");
            case CheckoutState.Cancelled:
                return;
        }

        using var response = await _http.PostAsJsonAsync(
            $"events/{eventId}/orders/{orderId}/cancel", new { refund = false }, JsonOptions, ct);

        if (response.StatusCode == HttpStatusCode.Conflict)
            return;

        await ReadOrderAsync(response, ct);
    }

    private CheckoutState ToState(HiEventsOrder order) => order.Status switch
    {
        "COMPLETED" when string.Equals(order.PaymentStatus, "PAYMENT_RECEIVED", StringComparison.Ordinal) => CheckoutState.Paid,
        "COMPLETED" => CheckoutState.Failed,
        "CANCELLED" => CheckoutState.Cancelled,
        "ABANDONED" => CheckoutState.Expired,
        "RESERVED" when ParseUtc(order.ReservedUntil) is { } until && until <= _clock.GetCurrentInstant()
            => CheckoutState.Expired,
        "RESERVED" => CheckoutState.Pending,
        _ => throw new HttpRequestException($"Hi.Events order {order.Id} has unknown status '{order.Status}'."),
    };

    private CheckoutOrderItem ToItem(CheckoutLine line)
    {
        var productId = line.Kind switch
        {
            CheckoutLineKind.Ticket => _settings.TicketProductId,
            CheckoutLineKind.Donation when _settings.DonationProductId > 0 => _settings.DonationProductId,
            _ => throw new TicketCheckoutException("HiEvents:DonationProductId is not set, so donation lines cannot be sold."),
        };

        return new CheckoutOrderItem(
            ProductId: productId,
            Price: line.AmountMinor / 100m,
            Attendee: line.AttendeeName is null ? null : new CheckoutAttendee(line.AttendeeName));
    }

    private async Task<HiEventsOrder> GetOrderAsync(int eventId, int orderId, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"events/{eventId}/orders/{orderId}", ct);
        return await ReadOrderAsync(response, ct);
    }

    private static async Task<HiEventsOrder> ReadOrderAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Hi.Events returned {(int)response.StatusCode}: {text}", null, response.StatusCode);
        }

        var envelope = await response.Content.ReadFromJsonAsync<Envelope>(JsonOptions, ct);
        return envelope?.Data ?? throw new HttpRequestException("Hi.Events returned an order response without data.");
    }

    private static string ToLocale(string culture)
    {
        var language = culture.Split('-', 2)[0].ToLowerInvariant();
        return SupportedLocales.Contains(language) ? language : "en";
    }

    /// <summary>Hi.Events stores times in UTC and serializes them as <c>yyyy-MM-dd HH:mm:ss</c>, without an offset.</summary>
    private static Instant? ParseUtc(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? Instant.FromDateTimeOffset(parsed)
            : null;

    private static long ToMinor(decimal amount) => (long)decimal.Round(amount * 100m, MidpointRounding.AwayFromZero);

    internal static string ToBackendOrderId(int eventId, int orderId) =>
        string.Create(CultureInfo.InvariantCulture, $"{eventId}:{orderId}");

    internal static (int EventId, int OrderId) ParseBackendOrderId(string backendOrderId)
    {
        var parts = backendOrderId.Split(':');
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var eventId)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var orderId))
        {
            return (eventId, orderId);
        }

        throw new ArgumentException($"'{backendOrderId}' is not a Hi.Events order id.", nameof(backendOrderId));
    }

    private sealed record CheckoutOrderBody(
        string ExternalReference,
        string FirstName,
        string? LastName,
        string Email,
        string Locale,
        string ExpiresAt,
        IReadOnlyList<CheckoutOrderItem> Items);

    private sealed record CheckoutOrderItem(int ProductId, decimal Price, CheckoutAttendee? Attendee);

    private sealed record CheckoutAttendee(string FirstName);

    private sealed record RefundBody(decimal Amount, bool NotifyBuyer, bool CancelOrder, string IdempotencyKey);

    private sealed record Envelope(HiEventsOrder? Data);

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
        string? ReservedUntil);
}
