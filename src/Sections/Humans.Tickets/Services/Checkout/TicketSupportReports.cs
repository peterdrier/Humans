using System.Globalization;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using NodaTime;

namespace Humans.Tickets.Services.Checkout;

/// <summary>The ticketing team's lookup and reports, computed in memory over one event's records.</summary>
internal static class TicketSupportReports
{
    /// <summary>Shorter queries match too much to be useful.</summary>
    public const int MinimumQueryLength = 3;

    /// <summary>Enough for a person to scan; a broader query should be narrowed, not paged.</summary>
    public const int MaxHits = 100;

    /// <summary>
    /// Matches an order id, its short reference or backend reference exactly, a ticket code exactly, or part of a buyer or holder name or
    /// email. Case and surrounding spaces are ignored. <paramref name="tickets"/> are the order's own.
    /// </summary>
    public static bool Matches(TicketCheckoutOrder order, IEnumerable<TicketCheckoutTicket> tickets, string query)
    {
        var q = query.Trim();
        if (q.Length < MinimumQueryLength)
            return false;

        if (Guid.TryParse(q, out var id))
            return order.Id == id;
        var own = tickets.ToList();
        if (Same(order.Reference, q) || Same(order.BackendOrderId, q) || own.Any(t => Same(t.Barcode, q)))
            return true;

        return Contains(order.BuyerName, q) || Contains(order.BuyerEmail, q)
            || order.Lines.Any(l => Contains(l.AttendeeName, q) || Contains(l.AttendeeEmail, q))
            || own.Any(t => Contains(t.HolderName, q) || Contains(t.HolderEmail, q));
    }

    public static IReadOnlyList<RefundListRow> RefundList(
        IReadOnlyList<TicketCheckoutOrder> orders, IReadOnlyList<TicketCheckoutRefund> refunds, RefundState? state)
    {
        var byId = orders.ToDictionary(o => o.Id);
        return refunds
            .Where(r => state is null || r.State == state)
            .Where(r => byId.ContainsKey(r.OrderId))
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new RefundListRow(r, byId[r.OrderId]))
            .ToList();
    }

    /// <summary>
    /// Each waiting offer's place, oldest first. An invited offer keeps its place: the invitation is only a second way
    /// to sell it.
    /// </summary>
    public static IReadOnlyDictionary<Guid, int> QueuePositions(IEnumerable<TicketResaleOffer> offers) =>
        offers
            .Where(o => o.State is TicketResaleOfferState.Open or TicketResaleOfferState.Reserved)
            .OrderBy(o => o.OfferedAt)
            .Select((o, i) => (o.Id, Position: i + 1))
            .ToDictionary(p => p.Id, p => p.Position);

    /// <summary>Waiting offers in selling order, then sold and withdrawn offers, newest first.</summary>
    public static IReadOnlyList<ResaleQueueRow> ResaleQueue(
        IReadOnlyList<TicketResaleOffer> offers,
        IReadOnlyList<TicketCheckoutTicket> tickets,
        IReadOnlyList<TicketCheckoutRefund> refunds,
        Instant now)
    {
        var byId = tickets.ToDictionary(t => t.Id);
        var refundsById = refunds.ToDictionary(r => r.Id);
        var positions = QueuePositions(offers);
        var waiting = offers
            .Where(o => positions.ContainsKey(o.Id))
            .OrderBy(o => o.OfferedAt)
            .Select(o => new ResaleQueueRow(o, byId.GetValueOrDefault(o.TicketId), positions[o.Id], o.HasLiveInvite, null));
        var closed = offers
            .Where(o => o.State is TicketResaleOfferState.Sold or TicketResaleOfferState.Withdrawn)
            .OrderByDescending(o => o.SoldAt ?? o.WithdrawnAt ?? o.OfferedAt)
            .Select(o => new ResaleQueueRow(o, byId.GetValueOrDefault(o.TicketId), null, false,
                o.RefundId is { } refundId ? refundsById.GetValueOrDefault(refundId) : null));
        return waiting.Concat(closed).ToList();
    }

    public static SalesReport Sales(
        IReadOnlyList<TicketCheckoutOrder> orders,
        IReadOnlyList<TicketCheckoutTicket> tickets,
        IReadOnlyList<TicketCheckoutRefund> refunds,
        IReadOnlyList<TicketResaleOffer> offers,
        DateTimeZone zone)
    {
        var paid = orders.Where(o => o.State == TicketCheckoutOrderState.Paid).ToList();
        var paidIds = paid.Select(o => o.Id).ToHashSet();
        var issued = tickets.Where(t => paidIds.Contains(t.OrderId)).ToList();
        var donations = paid.SelectMany(o => o.Lines.Where(l => l.Kind == CheckoutLineKind.Donation)).ToList();

        var byPrice = issued
            .GroupBy(PriceLabel, StringComparer.Ordinal)
            .Select(g => new SalesRow(g.Key, g.Count(), g.Sum(t => t.PricePaidMinor)))
            .OrderByDescending(r => r.Count).ThenBy(r => r.Label, StringComparer.Ordinal)
            .ToList();

        var byDay = paid
            .GroupBy(o => o.PaidAt!.Value.InZone(zone).Date)
            .OrderBy(g => g.Key)
            .Select(g => new DayRow(
                g.Key,
                g.Sum(o => o.Lines.Count(l => l.Kind == CheckoutLineKind.Ticket)),
                g.Sum(o => o.TicketTotalMinor),
                g.Sum(o => o.TotalMinor - o.TicketTotalMinor)))
            .ToList();

        var byPurpose = donations
            .GroupBy(l => l.DonationPurpose ?? TicketDonationPurpose.General)
            .OrderBy(g => g.Key)
            .Select(g => new DonationRow(g.Key, g.Count(), g.Sum(l => l.AmountMinor)))
            .ToList();

        var orderIds = orders.Select(o => o.Id).ToHashSet();
        var refundsByState = refunds
            .Where(r => orderIds.Contains(r.OrderId))
            .GroupBy(r => r.State)
            .OrderBy(g => g.Key)
            .Select(g => new RefundStateRow(g.Key, g.Count(), g.Sum(r => r.AmountMinor)))
            .ToList();

        var resaleByState = offers
            .GroupBy(o => o.State)
            .OrderBy(g => g.Key)
            .Select(g => new ResaleStateRow(g.Key, g.Count(), g.Sum(o => o.RefundOwedMinor)))
            .ToList();

        return new SalesReport(
            issued.Count,
            issued.Count(t => t.State is TicketCheckoutTicketState.Valid or TicketCheckoutTicketState.Offered),
            paid.Sum(o => o.TicketTotalMinor),
            donations.Sum(l => l.AmountMinor),
            paid.Sum(o => o.FeeMinor ?? 0),
            paid.Sum(o => o.RefundedMinor),
            paid.Sum(o => o.DisputedMinor),
            byPrice, byDay, byPurpose, refundsByState, resaleByState);
    }

    /// <summary>The class an adult ticket was priced at, else its wave; children and companions by kind.</summary>
    public static string PriceLabel(TicketCheckoutTicket ticket) => ticket.Kind switch
    {
        TicketHolderKind.Adult => ticket.ClassLabel ?? (ticket.WaveName.Length > 0 ? ticket.WaveName : "Unpriced"),
        _ => ticket.Kind.ToString(),
    };

    private static bool Same(string? value, string query) =>
        value is not null && string.Equals(value.Trim(), query, StringComparison.OrdinalIgnoreCase);

    /// <summary>Accent-insensitive, so "Garcia" finds "García".</summary>
    private static bool Contains(string? value, string query) =>
        value is not null
        && CultureInfo.InvariantCulture.CompareInfo.IndexOf(value, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
}

internal sealed record RefundListRow(TicketCheckoutRefund Refund, TicketCheckoutOrder Order);

/// <summary>
/// <paramref name="Position"/> is the place in the queue (see <see cref="TicketSupportReports.QueuePositions"/>), null
/// once closed. <paramref name="Refund"/> is the holder's refund on a sold offer, once requested.
/// </summary>
internal sealed record ResaleQueueRow(
    TicketResaleOffer Offer, TicketCheckoutTicket? Ticket, int? Position, bool DirectedLive, TicketCheckoutRefund? Refund);

/// <summary>
/// Totals over paid orders. <paramref name="TicketsIssued"/> counts every ticket ever issued (a resale issues a new
/// one); <paramref name="TicketsLive"/> only those still valid or offered. <paramref name="RefundedMinor"/> is what the
/// backends report as refunded, <paramref name="DisputedMinor"/> what buyers charged back; <paramref name="RefundsByState"/> is every refund Humans asked for.
/// </summary>
internal sealed record SalesReport(
    int TicketsIssued,
    int TicketsLive,
    long TicketRevenueMinor,
    long DonationsMinor,
    long FeesMinor,
    long RefundedMinor,
    long DisputedMinor,
    IReadOnlyList<SalesRow> ByPrice,
    IReadOnlyList<DayRow> ByDay,
    IReadOnlyList<DonationRow> DonationsByPurpose,
    IReadOnlyList<RefundStateRow> RefundsByState,
    IReadOnlyList<ResaleStateRow> ResaleByState);

/// <summary><paramref name="Label"/> is the wave or class the ticket was priced at.</summary>
internal sealed record SalesRow(string Label, int Count, long AmountMinor);

/// <summary>The day the order was paid, in the event's time zone.</summary>
internal sealed record DayRow(LocalDate Day, int Tickets, long TicketsMinor, long DonationsMinor);

internal sealed record DonationRow(TicketDonationPurpose Purpose, int Count, long AmountMinor);

internal sealed record RefundStateRow(RefundState State, int Count, long AmountMinor);

/// <summary><paramref name="OwedMinor"/> is what the holders paid: refunded on Sold, owed while waiting.</summary>
internal sealed record ResaleStateRow(TicketResaleOfferState State, int Count, long OwedMinor);
