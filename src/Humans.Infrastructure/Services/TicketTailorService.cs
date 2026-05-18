using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Humans.Application;
using Humans.Application.Configuration;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Tickets;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Infrastructure.Services;

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
        var settings1 = settings.Value;
        _logger = logger;

        var apiKey = settings1.ApiKey;
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
                    VendorOrderId: ticket.OrderId,
                    AttendeeName: ticket.FullName ?? $"{ticket.FirstName} {ticket.LastName}".Trim(),
                    AttendeeEmail: ResolveAttendeeEmail(ticket),
                    TicketTypeName: ticket.Description ?? "Unknown",
                    Price: (ticket.ListedPrice ?? 0) / 100m,
                    Status: ticket.Status ?? "valid",
                    CheckedInAt: ticket.CheckIn?.CheckedInAt is long epoch and > 0
                        ? Instant.FromUnixTimeSeconds(epoch)
                        : null));
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
        if (_cache.TryGetValue<VendorEventSummaryDto>(cacheKey, out var cachedSummary) &&
            cachedSummary is not null)
        {
            return cachedSummary;
        }

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

        var summary = new VendorEventSummaryDto(
            EventId: eventId,
            EventName: evt?.Name ?? "Unknown",
            TotalCapacity: totalCapacity,
            TicketsSold: ticketsSold,
            TicketsRemaining: totalCapacity - ticketsSold);

        _cache.Set(cacheKey, summary, EventSummaryCacheTtl);
        return summary;
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
        [property: JsonPropertyName("order_id")] string? OrderId,
        [property: JsonPropertyName("custom_questions")] List<TtCustomQuestion>? CustomQuestions,
        [property: JsonPropertyName("check_in")] TtCheckIn? CheckIn = null);

    // TicketTailor returns `check_in` as a nested object on issued_tickets when
    // a ticket has been scanned at the gate. `checked_in_at` is epoch seconds.
    // Absent / null when not checked in. Issue nobodies-collective/Humans#736.
    internal sealed record TtCheckIn(
        [property: JsonPropertyName("checked_in_at")] long? CheckedInAt);

    internal sealed record TtCustomQuestion(
        [property: JsonPropertyName("question")] string? Question,
        [property: JsonPropertyName("answer")] string? Answer);

    // TT's issued_ticket.email is the buyer/account email replicated onto every
    // ticket in the order — useless for matching the actual attendee. The real
    // attendee email is collected via a custom checkout question whose text is
    // exactly "Email" (see order or_76148796). Match the question string
    // verbatim; fall back to the top-level field when absent.
    internal static string? ResolveAttendeeEmail(TtIssuedTicket ticket)
    {
        var customEmail = ticket.CustomQuestions?
            .FirstOrDefault(q =>
                string.Equals(q.Question, "Email", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(q.Answer))
            ?.Answer
            ?.Trim();

        return !string.IsNullOrEmpty(customEmail) ? customEmail : ticket.Email;
    }

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

    public async Task<VoidIssuedTicketResult> VoidIssuedTicketAsync(
        string vendorTicketId, bool voidToHold, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/issued_tickets/{vendorTicketId}/void";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["void_to_hold"] = voidToHold ? "true" : "false",
        });

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync(url, content, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new TicketVendorWriteException(
                $"TicketTailor void transport failure: {ex.Message}",
                TicketVendorFailureKind.Transient, ex);
        }

        if (!response.IsSuccessStatusCode)
            throw await BuildVendorWriteExceptionAsync(response, "void", vendorTicketId, ct);

        var body = await response.Content.ReadFromJsonAsync<TtVoidResponse>(JsonOptions, ct);
        return new VoidIssuedTicketResult(
            VendorTicketId: body?.Id ?? vendorTicketId,
            HoldId: body?.HoldId);
    }

    public async Task<VendorTicketDto> IssueTicketAsync(
        IssueTicketRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.HoldId) &&
            (string.IsNullOrEmpty(request.EventId) || string.IsNullOrEmpty(request.TicketTypeId)))
        {
            throw new ArgumentException(
                "IssueTicketRequest requires either HoldId or both EventId and TicketTypeId.",
                nameof(request));
        }

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["full_name"] = request.FullName,
            ["send_email"] = request.SendEmail ? "true" : "false",
        };
        if (!string.IsNullOrEmpty(request.HoldId))
            form["hold_id"] = request.HoldId;
        else
        {
            form["event_id"] = request.EventId!;
            form["ticket_type_id"] = request.TicketTypeId!;
        }
        if (!string.IsNullOrEmpty(request.Email)) form["email"] = request.Email;
        if (!string.IsNullOrEmpty(request.ExternalReference)) form["reference"] = request.ExternalReference;

        using var content = new FormUrlEncodedContent(form);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync($"{BaseUrl}/issued_tickets", content, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new TicketVendorWriteException(
                $"TicketTailor issue transport failure: {ex.Message}",
                TicketVendorFailureKind.Transient, ex);
        }

        if (!response.IsSuccessStatusCode)
            throw await BuildVendorWriteExceptionAsync(response, "issue", request.FullName, ct);

        var body = await response.Content.ReadFromJsonAsync<TtIssuedTicket>(JsonOptions, ct)
            ?? throw new TicketVendorWriteException(
                "TicketTailor issue returned 2xx with empty body",
                TicketVendorFailureKind.Transient);

        return new VendorTicketDto(
            VendorTicketId: body.Id,
            VendorOrderId: body.OrderId,
            AttendeeName: body.FullName ?? $"{body.FirstName} {body.LastName}".Trim(),
            AttendeeEmail: ResolveAttendeeEmail(body),
            TicketTypeName: body.Description ?? "Unknown",
            Price: (body.ListedPrice ?? 0) / 100m,
            Status: body.Status ?? "valid");
    }

    internal sealed record TtVoidResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("hold_id")] string? HoldId,
        [property: JsonPropertyName("voided")] string? Voided);

    private static async Task<TicketVendorWriteException> BuildVendorWriteExceptionAsync(
        HttpResponseMessage response, string op, string subject, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var kind = (int)response.StatusCode switch
        {
            400 or 422 => TicketVendorFailureKind.Validation,
            401 or 403 => TicketVendorFailureKind.AuthFailed,
            404 => TicketVendorFailureKind.NotFound,
            429 => TicketVendorFailureKind.RateLimited,
            >= 500 => TicketVendorFailureKind.Transient,
            _ => TicketVendorFailureKind.Transient,
        };
        return new TicketVendorWriteException(
            $"TicketTailor {op} {subject} returned {(int)response.StatusCode}: {body}", kind);
    }
}
