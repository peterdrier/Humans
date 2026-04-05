using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Development-only ticket vendor stub backed by the local database.
/// It allows the Tickets UI to work with seeded local data without real TicketTailor credentials.
/// </summary>
public sealed class StubTicketVendorService : ITicketVendorService
{
    private readonly HumansDbContext _dbContext;
    private readonly TicketVendorSettings _settings;

    public StubTicketVendorService(
        HumansDbContext dbContext,
        IOptions<TicketVendorSettings> settings)
    {
        _dbContext = dbContext;
        _settings = settings.Value;
    }

    public async Task<IReadOnlyList<VendorOrderDto>> GetOrdersAsync(
        Instant? since,
        string eventId,
        CancellationToken ct = default)
    {
        var query = _dbContext.TicketOrders
            .AsNoTracking()
            .Where(o => o.VendorEventId == eventId);

        if (since.HasValue)
        {
            query = query.Where(o => o.SyncedAt >= since.Value);
        }

        var orders = await query
            .OrderBy(o => o.PurchasedAt)
            .ToListAsync(ct);

        return orders
            .Select(o => new VendorOrderDto(
                VendorOrderId: o.VendorOrderId,
                BuyerName: o.BuyerName,
                BuyerEmail: o.BuyerEmail,
                TotalAmount: o.TotalAmount,
                Currency: o.Currency,
                DiscountCode: o.DiscountCode,
                PaymentStatus: o.PaymentStatus switch
                {
                    TicketPaymentStatus.Paid => "completed",
                    TicketPaymentStatus.Pending => "pending",
                    TicketPaymentStatus.Refunded => "refunded",
                    TicketPaymentStatus.Cancelled => "cancelled",
                    _ => "pending"
                },
                VendorDashboardUrl: o.VendorDashboardUrl,
                PurchasedAt: o.PurchasedAt,
                Tickets: Array.Empty<VendorTicketDto>(),
                StripePaymentIntentId: o.StripePaymentIntentId,
                DiscountAmount: o.DiscountAmount,
                DonationAmount: o.DonationAmount))
            .ToList();
    }

    public async Task<IReadOnlyList<VendorTicketDto>> GetIssuedTicketsAsync(
        Instant? since,
        string eventId,
        CancellationToken ct = default)
    {
        var query = _dbContext.TicketAttendees
            .AsNoTracking()
            .Include(a => a.TicketOrder)
            .Where(a => a.VendorEventId == eventId);

        if (since.HasValue)
        {
            query = query.Where(a => a.SyncedAt >= since.Value);
        }

        var attendees = await query
            .OrderBy(a => a.TicketOrder.PurchasedAt)
            .ThenBy(a => a.AttendeeName)
            .ToListAsync(ct);

        return attendees
            .Select(a => new VendorTicketDto(
                VendorTicketId: a.VendorTicketId,
                VendorOrderId: a.TicketOrder.VendorOrderId,
                AttendeeName: a.AttendeeName,
                AttendeeEmail: a.AttendeeEmail,
                TicketTypeName: a.TicketTypeName,
                Price: a.Price,
                Status: a.Status switch
                {
                    TicketAttendeeStatus.Valid => "valid",
                    TicketAttendeeStatus.Void => "void",
                    TicketAttendeeStatus.CheckedIn => "checked_in",
                    _ => "void"
                }))
            .ToList();
    }

    public async Task<VendorEventSummaryDto> GetEventSummaryAsync(
        string eventId,
        CancellationToken ct = default)
    {
        var ticketsSold = await _dbContext.TicketAttendees
            .CountAsync(
                a => a.VendorEventId == eventId &&
                     (a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn),
                ct);

        var breakEvenTarget = _settings.BreakEvenTarget > 0 ? _settings.BreakEvenTarget : 2000;
        var totalCapacity = Math.Max(
            breakEvenTarget,
            ticketsSold > 0 ? (int)Math.Ceiling(ticketsSold / 0.30m) : breakEvenTarget);

        return new VendorEventSummaryDto(
            EventId: eventId,
            EventName: "Seeded Local Ticket Event",
            TotalCapacity: totalCapacity,
            TicketsSold: ticketsSold,
            TicketsRemaining: Math.Max(totalCapacity - ticketsSold, 0));
    }

    public Task<IReadOnlyList<string>> GenerateDiscountCodesAsync(
        DiscountCodeSpec spec,
        CancellationToken ct = default)
    {
        var prefix = spec.DiscountType == DiscountType.Percentage ? "PCT" : "FIX";
        IReadOnlyList<string> codes = Enumerable.Range(1, spec.Count)
            .Select(i => $"DEMO-{prefix}-{i:0000}")
            .ToList();
        return Task.FromResult(codes);
    }

    public async Task<IReadOnlyList<DiscountCodeStatusDto>> GetDiscountCodeUsageAsync(
        IEnumerable<string> codes,
        CancellationToken ct = default)
    {
        var codeList = codes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (codeList.Count == 0)
        {
            return Array.Empty<DiscountCodeStatusDto>();
        }

        var orderCounts = await _dbContext.TicketOrders
            .AsNoTracking()
            .Where(o => o.DiscountCode != null && codeList.Contains(o.DiscountCode))
            .GroupBy(o => o.DiscountCode!)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var lookup = orderCounts.ToDictionary(x => x.Code, x => x.Count, StringComparer.OrdinalIgnoreCase);

        return codeList
            .Select(code => new DiscountCodeStatusDto(
                Code: code,
                IsRedeemed: lookup.TryGetValue(code, out var count) && count > 0,
                TimesUsed: lookup.TryGetValue(code, out count) ? count : 0))
            .ToList();
    }
}
