using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using NodaTime;

namespace Humans.Tickets.Services.Checkout;

/// <summary>The ticketing team's lookup and reports, computed in memory over one event's orders.</summary>
internal static class TicketSupportReports
{
    /// <summary>Shorter queries match too much to be useful.</summary>
    public const int MinimumQueryLength = 3;

    /// <summary>
    /// Matches an order id or backend reference exactly, a ticket code exactly, or part of a buyer or holder name or
    /// email. Case and surrounding spaces are ignored.
    /// </summary>
    public static bool Matches(TicketCheckoutOrder order, string query)
    {
        var q = query.Trim();
        if (q.Length < MinimumQueryLength)
            return false;

        if (Guid.TryParse(q, out var id))
            return order.Id == id;
        if (Same(order.BackendOrderId, q) || order.Lines.Any(l => Same(l.Barcode, q)))
            return true;

        return Contains(order.BuyerName, q) || Contains(order.BuyerEmail, q)
            || order.Lines.Any(l => Contains(l.AttendeeName, q) || Contains(l.AttendeeEmail, q));
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

    public static SalesReport Sales(
        IReadOnlyList<TicketCheckoutOrder> orders, IReadOnlyList<TicketCheckoutRefund> refunds, DateTimeZone zone)
    {
        var paid = orders.Where(o => o.State == TicketCheckoutOrderState.Paid).ToList();
        var tickets = paid.SelectMany(o => o.Lines.Where(l => l.Kind == CheckoutLineKind.Ticket)).ToList();
        var donations = paid.SelectMany(o => o.Lines.Where(l => l.Kind == CheckoutLineKind.Donation)).ToList();

        var byPrice = tickets
            .GroupBy(l => l.Description, StringComparer.Ordinal)
            .Select(g => new SalesRow(g.Key, g.Count(), g.Sum(l => l.AmountMinor)))
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
        var byState = refunds
            .Where(r => orderIds.Contains(r.OrderId))
            .GroupBy(r => r.State)
            .OrderBy(g => g.Key)
            .Select(g => new RefundStateRow(g.Key, g.Count(), g.Sum(r => r.AmountMinor)))
            .ToList();

        return new SalesReport(
            tickets.Count,
            paid.Sum(o => o.TicketTotalMinor),
            donations.Sum(l => l.AmountMinor),
            paid.Sum(o => o.FeeMinor ?? 0),
            paid.Sum(o => o.RefundedMinor),
            byPrice, byDay, byPurpose, byState);
    }

    private static bool Same(string? value, string query) =>
        value is not null && string.Equals(value.Trim(), query, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string? value, string query) =>
        value is not null && value.Contains(query, StringComparison.OrdinalIgnoreCase);
}

internal sealed record RefundListRow(TicketCheckoutRefund Refund, TicketCheckoutOrder Order);

/// <summary>
/// Totals over paid orders. <paramref name="RefundedMinor"/> is what the backends report as refunded;
/// <paramref name="RefundsByState"/> is every refund Humans asked for, failed ones included.
/// </summary>
internal sealed record SalesReport(
    int TicketsSold,
    long TicketRevenueMinor,
    long DonationsMinor,
    long FeesMinor,
    long RefundedMinor,
    IReadOnlyList<SalesRow> ByPrice,
    IReadOnlyList<DayRow> ByDay,
    IReadOnlyList<DonationRow> DonationsByPurpose,
    IReadOnlyList<RefundStateRow> RefundsByState);

/// <summary><paramref name="Label"/> is the wave or class the ticket was priced at.</summary>
internal sealed record SalesRow(string Label, int Count, long AmountMinor);

/// <summary>The day the order was paid, in the event's time zone.</summary>
internal sealed record DayRow(LocalDate Day, int Tickets, long TicketsMinor, long DonationsMinor);

internal sealed record DonationRow(TicketDonationPurpose Purpose, int Count, long AmountMinor);

internal sealed record RefundStateRow(RefundState State, int Count, long AmountMinor);
