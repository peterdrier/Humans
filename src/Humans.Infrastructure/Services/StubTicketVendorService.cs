using System.Diagnostics;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Tickets;
using NodaTime;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Development-only ticket vendor stub that returns canned sample data.
/// The real TicketSyncService processes this through the normal sync pipeline
/// (upsert, email matching, VAT computation, etc.) so dev environments
/// exercise the production code path with realistic data.
/// </summary>
public sealed class StubTicketVendorService : ITicketVendorService
{
    private static readonly (string Name, decimal Price, int Count)[] TicketMix =
    [
        ("Low income", 100m, 90),
        ("Contributor", 250m, 150),
        ("Standard", 275m, 210),
        ("VIP", 400m, 150)
    ];

    private static readonly string[] FirstNames =
    [
        "Ariadna", "Mateo", "Lucia", "Pau", "Ines", "Jules", "Nora", "Leo", "Clara", "Hugo",
        "Noa", "Dario", "Marta", "Teo", "Sofia", "Bruno", "Laia", "Nico", "Mila", "Izan"
    ];

    private static readonly string[] LastNames =
    [
        "Soler", "Campos", "Navarro", "Torres", "Arias", "Costa", "Benet", "Ferrer", "Lopez", "Mora",
        "Sala", "Vidal", "Roig", "Pons", "Santos", "Prats", "Valle", "Luna", "Guasch", "Casals"
    ];

    // Pre-built sample data, generated once and cached for the process lifetime.
    private static readonly Lazy<SampleData> Sample = new(BuildSampleData);

    public Task<IReadOnlyList<VendorOrderDto>> GetOrdersAsync(
        Instant? since, string eventId, CancellationToken ct = default)
    {
        IReadOnlyList<VendorOrderDto> orders = since.HasValue
            ? Sample.Value.Orders.Where(o => o.PurchasedAt >= since.Value).ToList()
            : Sample.Value.Orders;

        return Task.FromResult(orders);
    }

    public Task<IReadOnlyList<VendorTicketDto>> GetIssuedTicketsAsync(
        Instant? since, string eventId, CancellationToken ct = default)
    {
        // Tickets don't have their own timestamp, so return all on first sync
        // and none on incremental (the sync service uses order timestamps).
        IReadOnlyList<VendorTicketDto> tickets = since.HasValue
            ? []
            : Sample.Value.Tickets;

        return Task.FromResult(tickets);
    }

    public Task<VendorEventSummaryDto> GetEventSummaryAsync(
        string eventId, CancellationToken ct = default)
    {
        var ticketsSold = Sample.Value.Tickets.Count(t =>
            string.Equals(t.Status, "valid", StringComparison.Ordinal) ||
            string.Equals(t.Status, "checked_in", StringComparison.Ordinal));

        return Task.FromResult(new VendorEventSummaryDto(
            EventId: eventId,
            EventName: "Demo Event (Stub)",
            TotalCapacity: 2000,
            TicketsSold: ticketsSold,
            TicketsRemaining: 2000 - ticketsSold));
    }

    public Task<IReadOnlyList<string>> GenerateDiscountCodesAsync(
        DiscountCodeSpec spec, CancellationToken ct = default)
    {
        var prefix = spec.DiscountType == DiscountType.Percentage ? "PCT" : "FIX";
        IReadOnlyList<string> codes = Enumerable.Range(1, spec.Count)
            .Select(i => $"DEMO-{prefix}-{i:0000}")
            .ToList();
        return Task.FromResult(codes);
    }

    public Task<IReadOnlyList<DiscountCodeStatusDto>> GetDiscountCodeUsageAsync(
        IEnumerable<string> codes, CancellationToken ct = default)
    {
        IReadOnlyList<DiscountCodeStatusDto> result = codes
            .Select(c => new DiscountCodeStatusDto(Code: c, IsRedeemed: false, TimesUsed: 0))
            .ToList();
        return Task.FromResult(result);
    }

    private static SampleData BuildSampleData()
    {
        var orders = new List<VendorOrderDto>();
        var tickets = new List<VendorTicketDto>();

        // Build the ticket pool with the defined mix
        var ticketPool = new List<(string Type, decimal Price)>();
        foreach (var (name, price, count) in TicketMix)
        {
            for (var i = 0; i < count; i++)
                ticketPool.Add((name, price));
        }

        // Deterministic shuffle
        ticketPool = ticketPool
            .Select((t, i) => (t, Sort: DeterministicHash($"shuffle:{i}")))
            .OrderBy(x => x.Sort)
            .Select(x => x.t)
            .ToList();

        // Build orders: 150 two-ticket + 300 single-ticket = 600 tickets total
        var orderSizes = Enumerable.Repeat(2, 150)
            .Concat(Enumerable.Repeat(1, 300))
            .Select((size, i) => (Size: size, Sort: DeterministicHash($"order-size:{i}")))
            .OrderBy(x => x.Sort)
            .Select(x => x.Size)
            .ToList();

        var ticketCursor = 0;
        var saleStart = new LocalDate(2026, 3, 14);

        for (var orderIndex = 0; orderIndex < orderSizes.Count; orderIndex++)
        {
            var size = orderSizes[orderIndex];
            var orderTickets = ticketPool.Skip(ticketCursor).Take(size).ToList();
            ticketCursor += size;

            var vendorOrderId = $"stub-order-{orderIndex + 1:D4}";
            var buyer = BuildPerson(orderIndex);
            var purchasedAt = BuildPurchaseInstant(saleStart, orderIndex, orderSizes.Count);

            // Some orders get donations
            var donation = GetDonation(orderIndex, orderTickets);

            // Some orders get discount codes
            string? discountCode = null;
            decimal? discountAmount = null;
            if (orderIndex % 11 == 0)
            {
                discountCode = $"COMMUNITY-2026-{orderIndex + 1:D3}";
                discountAmount = 30m;
            }

            var ticketTotal = orderTickets.Sum(t => t.Price);
            var totalAmount = Math.Round(ticketTotal - (discountAmount ?? 0m) + donation, 2);

            var vendorTickets = new List<VendorTicketDto>();
            for (var t = 0; t < orderTickets.Count; t++)
            {
                var ticket = orderTickets[t];
                var attendee = BuildPerson(orderIndex * 10 + t + 500);
                var vendorTicketId = $"stub-ticket-{orderIndex + 1:D4}-{t + 1:D2}";

                // Every 5th ticket is checked in for realistic event summary stats
                var status = (orderIndex * 10 + t) % 5 == 0 ? "checked_in" : "valid";

                var ticketDto = new VendorTicketDto(
                    VendorTicketId: vendorTicketId,
                    VendorOrderId: vendorOrderId,
                    AttendeeName: attendee.Name,
                    AttendeeEmail: attendee.Email,
                    TicketTypeName: ticket.Type,
                    Price: ticket.Price,
                    Status: status);

                vendorTickets.Add(ticketDto);
                tickets.Add(ticketDto);
            }

            orders.Add(new VendorOrderDto(
                VendorOrderId: vendorOrderId,
                BuyerName: buyer.Name,
                BuyerEmail: buyer.Email,
                TotalAmount: totalAmount,
                Currency: "EUR",
                DiscountCode: discountCode,
                PaymentStatus: "completed",
                VendorDashboardUrl: $"https://demo.tickettailor.local/orders/{vendorOrderId}",
                PurchasedAt: purchasedAt,
                Tickets: vendorTickets,
                StripePaymentIntentId: null,
                DiscountAmount: discountAmount,
                DonationAmount: donation));
        }

        Debug.Assert(ticketCursor == ticketPool.Count,
            $"Ticket pool size mismatch: cursor {ticketCursor} != pool size {ticketPool.Count}");

        // Add a few non-paid orders
        var nonPaidStatuses = new[] { "pending", "pending", "refunded", "cancelled" };
        for (var i = 0; i < nonPaidStatuses.Length; i++)
        {
            var idx = orderSizes.Count + i + 1;
            var vendorOrderId = $"stub-order-{idx:D4}";
            var buyer = BuildPerson(700 + i);
            var ticket = TicketMix[i % TicketMix.Length];
            var vendorTicketId = $"stub-ticket-{idx:D4}-01";

            var ticketDto = new VendorTicketDto(
                VendorTicketId: vendorTicketId,
                VendorOrderId: vendorOrderId,
                AttendeeName: buyer.Name,
                AttendeeEmail: buyer.Email,
                TicketTypeName: ticket.Name,
                Price: ticket.Price,
                Status: "void");

            tickets.Add(ticketDto);

            orders.Add(new VendorOrderDto(
                VendorOrderId: vendorOrderId,
                BuyerName: buyer.Name,
                BuyerEmail: buyer.Email,
                TotalAmount: ticket.Price,
                Currency: "EUR",
                DiscountCode: null,
                PaymentStatus: nonPaidStatuses[i],
                VendorDashboardUrl: null,
                PurchasedAt: Instant.FromUtc(2026, 4, 1 + i, 10, 0),
                Tickets: [ticketDto]));
        }

        return new SampleData(orders, tickets);
    }

    private static decimal GetDonation(int orderIndex, List<(string Type, decimal Price)> orderTickets)
    {
        var hasVip = orderTickets.Any(t => string.Equals(t.Type, "VIP", StringComparison.Ordinal));
        if (hasVip && orderIndex % 2 == 0) return 100m;
        if (orderIndex % 4 == 0) return 25m;
        return 0m;
    }

    private static Instant BuildPurchaseInstant(LocalDate saleStart, int orderIndex, int totalOrders)
    {
        // Spread orders across ~3 weeks with front-loading
        var daysSpan = 22;
        var progress = (double)orderIndex / totalOrders;
        // Front-loaded curve: more orders in the first few days
        var day = (int)(Math.Pow(progress, 0.6) * daysSpan);
        var hour = 9 + (orderIndex % 9);
        var minute = (orderIndex * 11) % 60;

        return saleStart.PlusDays(day)
            .At(new LocalTime(hour, minute))
            .InZoneStrictly(DateTimeZone.Utc)
            .ToInstant();
    }

    private static (string Name, string Email) BuildPerson(int seed)
    {
        var first = FirstNames[(seed * 7) % FirstNames.Length];
        var last = LastNames[(seed * 11) % LastNames.Length];
        var name = $"{first} {last}";
        var slug = $"{first.ToLowerInvariant()}.{last.ToLowerInvariant()}";
        var email = $"{slug}.{seed:D4}@ticketstub.local";
        return (name, email);
    }

    private static int DeterministicHash(string value)
    {
        // Simple deterministic hash for shuffling — not crypto
        var hash = 17;
        foreach (var c in value)
            hash = (hash * 31) + c;
        return hash;
    }

    private sealed record SampleData(
        IReadOnlyList<VendorOrderDto> Orders,
        IReadOnlyList<VendorTicketDto> Tickets);
}
