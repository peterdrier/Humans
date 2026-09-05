using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Humans.Base.Caching;
using Humans.Base.Extensions;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.TicketTailor.Services;

/// <summary>
/// Ticket Tailor v1 client: one method per <see cref="ITicketVendorService"/> port method.
/// Reads throw <see cref="HttpRequestException"/>; void and issue throw
/// <see cref="TicketVendorWriteException"/>.
/// </summary>
internal sealed class TicketTailorService : ITicketVendorService
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
        _logger = logger;

        var apiKey = settings.Value.ApiKey;
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
        using var _ = _logger.TimeOperation();
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
        using var _ = _logger.TimeOperation();
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
                    Barcode: ticket.Barcode));
            }

            cursor = body.Links?.Next is not null ? body.Data[^1].Id : null;
        } while (cursor is not null);

        _logger.LogInformation("Fetched {Count} issued tickets from TicketTailor for event {EventId}",
            tickets.Count, eventId);

        return tickets;
    }

    public async Task<IReadOnlyList<VendorCheckInDto>> GetCheckInsAsync(
        Instant? since, string eventId, CancellationToken ct = default)
    {
        using var _ = _logger.TimeOperation();
        var records = new List<TtCheckIn>();
        string? cursor = null;

        do
        {
            var url = $"{BaseUrl}/check_ins?event_id={eventId}";
            // Page by created_at, not check_in_at: an offline scanner uploads scans whose
            // check_in_at predates our last sync, and a check_in_at cursor would drop them forever.
            if (since.HasValue)
                url += $"&created_at.gte={since.Value.ToUnixTimeSeconds()}";
            if (cursor is not null)
                url += $"&starting_after={cursor}";

            var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<TtPaginatedResponse<TtCheckIn>>(JsonOptions, ct);
            if (body?.Data is null || body.Data.Count == 0)
                break;

            records.AddRange(body.Data);

            cursor = body.Links?.Next is not null ? body.Data[^1].Id : null;
        } while (cursor is not null);

        var checkIns = NetCheckIns(records);

        _logger.LogInformation("Fetched {Count} check-ins from TicketTailor for event {EventId}",
            checkIns.Count, eventId);

        return checkIns;
    }

    // TicketTailor /check_ins includes checkout/undo records (quantity = -1) alongside
    // check-ins (quantity = +1, or more for a group ticket). Net the quantity per issued
    // ticket and report a check-in only when the net is positive — otherwise a checkout
    // record would wrongly mark the attendee onsite. The recorded arrival is the earliest
    // positive scan (check_in_at, falling back to created_at).
    private static IReadOnlyList<VendorCheckInDto> NetCheckIns(IEnumerable<TtCheckIn> records)
    {
        var result = new List<VendorCheckInDto>();

        foreach (var group in records
                     .Where(r => r.IssuedTicketId is { Length: > 0 })
                     .GroupBy(r => r.IssuedTicketId!, StringComparer.Ordinal))
        {
            if (group.Sum(r => r.Quantity ?? 1) <= 0)
                continue;

            var earliest = group
                .Where(r => (r.Quantity ?? 1) > 0)
                .Select(r => r.CheckInAt ?? r.CreatedAt)
                .Where(e => e is > 0)
                .Select(e => e!.Value)
                .DefaultIfEmpty(0L)
                .Min();

            if (earliest > 0)
                result.Add(new VendorCheckInDto(group.Key, Instant.FromUnixTimeSeconds(earliest)));
        }

        return result;
    }

    public async Task<VendorEventSummaryDto> GetEventSummaryAsync(
        string eventId, CancellationToken ct = default)
    {
        using var _ = _logger.TimeOperation();
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

            response.EnsureSuccessStatusCode();
        }

        var evt = await response.Content.ReadFromJsonAsync<TtEvent>(JsonOptions, ct);

        // Capacity comes from ticket_groups (waves share the same pool).
        // Summing ticket_types.quantity_total is wrong — waves are subdivisions, not additive.
        var totalCapacity = evt?.TicketGroups?.Sum(g => g.MaxQuantity ?? 0) ?? 0;
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
        using var _ = _logger.TimeOperation();
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
        using var _ = _logger.TimeOperation();
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

    public async Task CreateCheckInAsync(
        string vendorTicketId, Instant occurredAt, CancellationToken ct = default)
    {
        using var _ = _logger.TimeOperation();

        // Form-encoded, not JSON — a JSON body silently 400s. issued_ticket_id and quantity are
        // required; check_in_at is unix seconds. Not idempotent (each POST creates a record), so
        // callers never retry. The key needs Event-manager scope; an Order-manager key 403s.
        var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["issued_ticket_id"] = vendorTicketId,
            ["quantity"] = "1",
            ["check_in_at"] = occurredAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        });

        var response = await _httpClient.PostAsync($"{BaseUrl}/check_ins", form, ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "TicketTailor check-in API returned {StatusCode} for issued ticket {VendorTicketId}",
                (int)response.StatusCode, vendorTicketId);
            response.EnsureSuccessStatusCode();
        }

        _logger.LogInformation(
            "Recorded TicketTailor check-in for issued ticket {VendorTicketId}", vendorTicketId);
    }

    /// <summary>
    /// TT puts the code in a "gift_card" line item's description, in parentheses:
    /// "NCA Contributor Discount (DISC25-OPGYT8-004)".
    /// </summary>
    private static string? ExtractDiscountCode(List<TtLineItem>? lineItems)
    {
        var discountItem = lineItems?.FirstOrDefault(li =>
            string.Equals(li.Type, "gift_card", StringComparison.OrdinalIgnoreCase));

        if (discountItem?.Description is null) return null;

        var openParen = discountItem.Description.LastIndexOf('(');
        var closeParen = discountItem.Description.LastIndexOf(')');
        if (openParen >= 0 && closeParen > openParen)
            return discountItem.Description[(openParen + 1)..closeParen];

        return discountItem.Description;
    }

    /// <summary>gift_card totals are negative in the API; null when no discount applied.</summary>
    private static decimal? ExtractDiscountAmount(List<TtLineItem>? lineItems)
    {
        if (lineItems is null) return null;

        var discountCents = lineItems
            .Where(li => string.Equals(li.Type, "gift_card", StringComparison.OrdinalIgnoreCase))
            .Sum(li => Math.Abs(li.Total ?? 0));

        return discountCents > 0 ? discountCents / 100m : null;
    }

    /// <summary>Standalone "donation" line items (VAT-exempt add-ons); 0 when none.</summary>
    private static decimal ExtractDonationAmount(List<TtLineItem>? lineItems)
    {
        if (lineItems is null) return 0m;

        var donationCents = lineItems
            .Where(li => string.Equals(li.Type, "donation", StringComparison.OrdinalIgnoreCase))
            .Sum(li => li.Total ?? 0);

        return donationCents > 0 ? donationCents / 100m : 0m;
    }

    // Wire records: internal (not private) for System.Text.Json; names follow JsonOptions'
    // snake_case policy.

    internal sealed record TtPaginatedResponse<T>(
        List<T> Data,
        TtLinks? Links);

    internal sealed record TtLinks(
        string? Next);

    internal sealed record TtOrder(
        string Id,
        TtBuyerDetails? BuyerDetails,
        int? Total,
        TtCurrency? Currency,
        string? Status,
        long CreatedAt,
        List<TtLineItem>? LineItems,
        string? TxnId);

    internal sealed record TtLineItem(
        string? Description,
        string? Type,
        int? Total);

    internal sealed record TtBuyerDetails(
        string? FirstName,
        string? LastName,
        string? Email,
        string? Name);

    internal sealed record TtCurrency(
        string? Code);

    internal sealed record TtIssuedTicket(
        string Id,
        string? FirstName,
        string? LastName,
        string? FullName,
        string? Email,
        string? Description,
        int? ListedPrice,
        string? Status,
        string? OrderId,
        List<TtCustomQuestion>? CustomQuestions,
        string? Barcode = null);

    // Gate scans are their own /check_ins resource; the issued ticket stays "valid".
    // check_in_at and created_at are epoch seconds.
    internal sealed record TtCheckIn(
        string Id,
        string? IssuedTicketId,
        long? CheckInAt,
        long? CreatedAt,
        int? Quantity);

    internal sealed record TtCustomQuestion(
        string? Question,
        string? Answer);

    // TT's issued_ticket.email is the buyer/account email replicated onto every
    // ticket in the order — useless for matching the actual attendee. The real
    // attendee email is collected via a custom checkout question whose text is
    // exactly "Email". Match the question string verbatim; fall back to the
    // top-level field when absent.
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
        string? Name,
        int? TotalIssuedTickets,
        List<TtTicketType>? TicketTypes,
        List<TtTicketGroup>? TicketGroups);

    internal sealed record TtTicketType(
        int? QuantityTotal);

    internal sealed record TtTicketGroup(
        int? MaxQuantity);

    internal sealed record TtVoucherCode(
        string? Code,
        int? TimesUsed);

    public async Task<VoidIssuedTicketResult> VoidIssuedTicketAsync(
        string vendorTicketId, bool voidToHold, CancellationToken ct = default)
    {
        using var _ = _logger.TimeOperation();
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
        using var _ = _logger.TimeOperation();
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
        string? Id,
        string? HoldId);

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
