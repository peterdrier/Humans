using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Infrastructure.Services;

public class TicketVendorSettings
{
    public const string SectionName = "TicketVendor";

    public string Provider { get; set; } = "TicketTailor";
    public string EventId { get; set; } = string.Empty;
    public int SyncIntervalMinutes { get; set; } = 15;
    public int BreakEvenTarget { get; set; }
    /// <summary>API key — populated from TICKET_VENDOR_API_KEY env var at DI registration time.
    /// Not stored in appsettings (sensitive). Accessible in settings for testability.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrEmpty(EventId) && !string.IsNullOrEmpty(ApiKey);
}

/// <summary>
/// TicketTailor API client implementing the vendor-agnostic interface.
/// API key comes from TICKET_VENDOR_API_KEY environment variable.
/// Non-sensitive config comes from appsettings TicketVendor section.
/// </summary>
public class TicketTailorService : ITicketVendorService
{
    private const string BaseUrl = "https://api.tickettailor.com/v1";
    private static readonly TimeSpan EventSummaryCacheTtl = TimeSpan.FromMinutes(15);

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TicketTailorService> _logger;
    private readonly TicketVendorSettings _settings;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public TicketTailorService(
        HttpClient httpClient,
        IOptions<TicketVendorSettings> settings,
        IMemoryCache cache,
        ILogger<TicketTailorService> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _settings = settings.Value;
        _logger = logger;

        var apiKey = _settings.ApiKey;
        if (!string.IsNullOrEmpty(apiKey))
        {
            var authBytes = Encoding.ASCII.GetBytes($"{apiKey}:");
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        }
    }

    public async Task<IReadOnlyList<VendorOrderDto>> GetOrdersAsync(
        Instant? since, string eventId, CancellationToken ct = default)
    {
        var orders = new List<VendorOrderDto>();
        string? cursor = null;

        do
        {
            var url = $"{BaseUrl}/orders?event_id={eventId}";
            if (since.HasValue)
                url += $"&updated_at.gte={since.Value.ToUnixTimeSeconds()}";
            if (cursor is not null)
                url += $"&starting_after={cursor}";

            var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<TtPaginatedResponse<TtOrder>>(JsonOptions, ct);
            if (body?.Data is null || body.Data.Count == 0)
                break;

            foreach (var order in body.Data)
            {
                var purchasedAt = Instant.FromUnixTimeSeconds(order.CreatedAt);
                var buyer = order.BuyerDetails;

                // Discount codes are in line_items with type "gift_card",
                // code embedded in description like "NCA Contributor Discount (DISC25-OPGYT8-004)"
                var discountCode = ExtractDiscountCode(order.LineItems);
                var discountAmount = ExtractDiscountAmount(order.LineItems);
                var donationAmount = ExtractDonationAmount(order.LineItems);

                orders.Add(new VendorOrderDto(
                    VendorOrderId: order.Id,
                    BuyerName: buyer?.Name ?? $"{buyer?.FirstName} {buyer?.LastName}".Trim(),
                    BuyerEmail: buyer?.Email ?? string.Empty,
                    TotalAmount: (order.Total ?? 0) / 100m, // TT stores amounts in cents
                    Currency: order.Currency?.Code?.ToUpperInvariant() ?? "EUR",
                    DiscountCode: discountCode,
                    PaymentStatus: order.Status ?? "completed",
                    VendorDashboardUrl: null, // TT doesn't expose dashboard URLs via API
                    PurchasedAt: purchasedAt,
                    Tickets: [],
                    StripePaymentIntentId: order.TxnId,
                    DiscountAmount: discountAmount,
                    DonationAmount: donationAmount));
            }

            cursor = body.Links?.Next is not null ? body.Data[^1].Id : null;
        } while (cursor is not null);

        _logger.LogInformation("Fetched {Count} orders from TicketTailor for event {EventId}",
            orders.Count, eventId);

        return orders;
    }

    public async Task<IReadOnlyList<VendorTicketDto>> GetIssuedTicketsAsync(
        Instant? since, string eventId, CancellationToken ct = default)
    {
        var tickets = new List<VendorTicketDto>();
        string? cursor = null;

        do
        {
            var url = $"{BaseUrl}/issued_tickets?event_id={eventId}";
            if (since.HasValue)
                url += $"&updated_at.gte={since.Value.ToUnixTimeSeconds()}";
            if (cursor is not null)
                url += $"&starting_after={cursor}";

            var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<TtPaginatedResponse<TtIssuedTicket>>(JsonOptions, ct);
            if (body?.Data is null || body.Data.Count == 0)
                break;

            foreach (var ticket in body.Data)
            {
                tickets.Add(new VendorTicketDto(
                    VendorTicketId: ticket.Id,
                    VendorOrderId: ticket.OrderId ?? string.Empty,
                    AttendeeName: ticket.FullName ?? $"{ticket.FirstName} {ticket.LastName}".Trim(),
                    AttendeeEmail: ticket.Email,
                    TicketTypeName: ticket.Description ?? "Unknown",
                    Price: (ticket.ListedPrice ?? 0) / 100m,
                    Status: ticket.Status ?? "valid"));
            }

            cursor = body.Links?.Next is not null ? body.Data[^1].Id : null;
        } while (cursor is not null);

        _logger.LogInformation("Fetched {Count} issued tickets from TicketTailor for event {EventId}",
            tickets.Count, eventId);

        return tickets;
    }

    public async Task<VendorEventSummaryDto> GetEventSummaryAsync(
        string eventId, CancellationToken ct = default)
    {
        var cacheKey = CacheKeys.TicketEventSummary(eventId);
        return await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = EventSummaryCacheTtl;

            var response = await _httpClient.GetAsync($"{BaseUrl}/events/{eventId}", ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "TicketTailor event summary API returned {StatusCode} for event {EventId}",
                    (int)response.StatusCode, eventId);

                if ((int)response.StatusCode >= 500)
                    return new VendorEventSummaryDto(eventId, "Unknown", 0, 0, 0);

                response.EnsureSuccessStatusCode();
            }

            var evt = await response.Content.ReadFromJsonAsync<TtEvent>(JsonOptions, ct);

            // Capacity comes from ticket_groups (waves share the same pool).
            // Summing ticket_types.quantity_total is wrong — waves are subdivisions, not additive.
            var totalCapacity = evt?.TicketGroups?.Sum(g => g.MaxQuantity ?? 0) ?? 0;
            // Fall back to ungrouped ticket types if no groups defined
            if (totalCapacity == 0)
                totalCapacity = evt?.TicketTypes?.Sum(tt => tt.QuantityTotal ?? 0) ?? 0;
            var ticketsSold = evt?.TotalIssuedTickets ?? 0;

            return new VendorEventSummaryDto(
                EventId: eventId,
                EventName: evt?.Name ?? "Unknown",
                TotalCapacity: totalCapacity,
                TicketsSold: ticketsSold,
                TicketsRemaining: totalCapacity - ticketsSold);
        }) ?? new VendorEventSummaryDto(eventId, "Unknown", 0, 0, 0);
    }

    public async Task<IReadOnlyList<string>> GenerateDiscountCodesAsync(
        DiscountCodeSpec spec, CancellationToken ct = default)
    {
        var codes = new List<string>();
        for (var i = 0; i < spec.Count; i++)
        {
            var payload = new
            {
                code = $"NOBO-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                type = spec.DiscountType == DiscountType.Percentage ? "percentage" : "monetary",
                value = spec.DiscountType == DiscountType.Percentage
                    ? spec.DiscountValue
                    : spec.DiscountValue * 100, // TT uses cents for monetary
            };

            var response = await _httpClient.PostAsJsonAsync(
                $"{BaseUrl}/voucher_codes", payload, JsonOptions, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<TtVoucherCode>(JsonOptions, ct);
            if (result?.Code is not null)
                codes.Add(result.Code);
        }

        _logger.LogInformation("Generated {Count} discount codes via TicketTailor", codes.Count);
        return codes;
    }

    public async Task<IReadOnlyList<DiscountCodeStatusDto>> GetDiscountCodeUsageAsync(
        IEnumerable<string> codes, CancellationToken ct = default)
    {
        var results = new List<DiscountCodeStatusDto>();

        foreach (var code in codes)
        {
            var response = await _httpClient.GetAsync(
                $"{BaseUrl}/voucher_codes?code={Uri.EscapeDataString(code)}", ct);

            if (!response.IsSuccessStatusCode)
            {
                results.Add(new DiscountCodeStatusDto(code, false, 0));
                continue;
            }

            var body = await response.Content.ReadFromJsonAsync<TtPaginatedResponse<TtVoucherCode>>(JsonOptions, ct);
            var vc = body?.Data?.FirstOrDefault();
            results.Add(new DiscountCodeStatusDto(
                code,
                (vc?.TimesUsed ?? 0) > 0,
                vc?.TimesUsed ?? 0));
        }

        return results;
    }

    /// <summary>
    /// Extract discount code from line_items. TT puts discount codes in line items
    /// with type "gift_card" and the code in parentheses in the description,
    /// e.g. "NCA Contributor Discount (DISC25-OPGYT8-004)".
    /// </summary>
    private static string? ExtractDiscountCode(List<TtLineItem>? lineItems)
    {
        var discountItem = lineItems?.FirstOrDefault(li =>
            string.Equals(li.Type, "gift_card", StringComparison.OrdinalIgnoreCase));

        if (discountItem?.Description is null) return null;

        // Extract code from parentheses: "Some Label (CODE123)" → "CODE123"
        var openParen = discountItem.Description.LastIndexOf('(');
        var closeParen = discountItem.Description.LastIndexOf(')');
        if (openParen >= 0 && closeParen > openParen)
            return discountItem.Description[(openParen + 1)..closeParen];

        return discountItem.Description;
    }

    /// <summary>
    /// Sum the absolute value of gift_card line item totals (they're negative in the API).
    /// Returns null if no discount was applied.
    /// </summary>
    private static decimal? ExtractDiscountAmount(List<TtLineItem>? lineItems)
    {
        if (lineItems is null) return null;

        var discountCents = lineItems
            .Where(li => string.Equals(li.Type, "gift_card", StringComparison.OrdinalIgnoreCase))
            .Sum(li => Math.Abs(li.Total ?? 0));

        return discountCents > 0 ? discountCents / 100m : null;
    }

    /// <summary>
    /// Sum standalone donation line items from TT (type "donation").
    /// These are VAT-exempt add-on donations. Returns 0 if none.
    /// TT amounts are in cents — converted to euros.
    /// </summary>
    private static decimal ExtractDonationAmount(List<TtLineItem>? lineItems)
    {
        if (lineItems is null) return 0m;

        var donationCents = lineItems
            .Where(li => string.Equals(li.Type, "donation", StringComparison.OrdinalIgnoreCase))
            .Sum(li => li.Total ?? 0);

        return donationCents > 0 ? donationCents / 100m : 0m;
    }

    // --- TicketTailor API response models ---
    // Must be internal (not private) for System.Text.Json deserialization

    internal sealed record TtPaginatedResponse<T>(
        [property: JsonPropertyName("data")] List<T> Data,
        [property: JsonPropertyName("links")] TtLinks? Links);

    internal sealed record TtLinks(
        [property: JsonPropertyName("next")] string? Next,
        [property: JsonPropertyName("previous")] string? Previous);

    internal sealed record TtOrder(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("buyer_details")] TtBuyerDetails? BuyerDetails,
        [property: JsonPropertyName("total")] int? Total,
        [property: JsonPropertyName("currency")] TtCurrency? Currency,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("created_at")] long CreatedAt,
        [property: JsonPropertyName("line_items")] List<TtLineItem>? LineItems,
        [property: JsonPropertyName("txn_id")] string? TxnId);

    internal sealed record TtLineItem(
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("total")] int? Total);

    internal sealed record TtBuyerDetails(
        [property: JsonPropertyName("first_name")] string? FirstName,
        [property: JsonPropertyName("last_name")] string? LastName,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("name")] string? Name);

    internal sealed record TtCurrency(
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("base_multiplier")] int? BaseMultiplier);

    internal sealed record TtIssuedTicket(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("first_name")] string? FirstName,
        [property: JsonPropertyName("last_name")] string? LastName,
        [property: JsonPropertyName("full_name")] string? FullName,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("listed_price")] int? ListedPrice,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("order_id")] string? OrderId);

    internal sealed record TtEvent(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("total_holds")] int? TotalHolds,
        [property: JsonPropertyName("total_issued_tickets")] int? TotalIssuedTickets,
        [property: JsonPropertyName("total_orders")] int? TotalOrders,
        [property: JsonPropertyName("ticket_types")] List<TtTicketType>? TicketTypes,
        [property: JsonPropertyName("ticket_groups")] List<TtTicketGroup>? TicketGroups);

    internal sealed record TtTicketType(
        [property: JsonPropertyName("quantity_total")] int? QuantityTotal,
        [property: JsonPropertyName("quantity_issued")] int? QuantityIssued);

    internal sealed record TtTicketGroup(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("max_quantity")] int? MaxQuantity);

    internal sealed record TtVoucherCode(
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("times_used")] int? TimesUsed);
}
