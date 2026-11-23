using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using NodaTime;

namespace Humans.Tickets.Services.Checkout;

/// <summary>What one side of a reconciliation counts as sold: paid orders only, refunds and chargebacks against them.</summary>
internal sealed record TicketReconTotals(int Orders, int Tickets, long GrossMinor, long RefundedMinor, long DisputedMinor)
{
    public long NetMinor => GrossMinor - RefundedMinor - DisputedMinor;
}

internal enum TicketReconIssue
{
    /// <summary>Humans opened it on the backend, which no longer lists it.</summary>
    MissingAtBackend,
    /// <summary>The backend holds it, and it is no Humans order on this backend.</summary>
    ExtraAtBackend,
    /// <summary>A second backend order for the same Humans order.</summary>
    Duplicate,
    /// <summary>
    /// The backend holds an order for it that Humans never recorded: a create whose answer was lost, or one beside the
    /// recorded one the backend no longer lists.
    /// </summary>
    NotRecorded,
    /// <summary>Paid on one side and not the other.</summary>
    PaidMismatch,
    TotalMismatch,
    RefundMismatch,
    DisputeMismatch,
    TicketCountMismatch,
    /// <summary>Paid, but Humans has not issued one ticket per ticket line.</summary>
    TicketsNotIssued,
}

/// <summary>The words the reconciliation page and the ticketing team's alert use for each difference.</summary>
internal static class TicketReconLabels
{
    public static string Label(this TicketReconIssue issue) => issue switch
    {
        TicketReconIssue.MissingAtBackend => "missing at backend",
        TicketReconIssue.ExtraAtBackend => "extra at backend",
        TicketReconIssue.Duplicate => "duplicate",
        TicketReconIssue.NotRecorded => "backend order not recorded in Humans",
        TicketReconIssue.PaidMismatch => "paid on one side only",
        TicketReconIssue.TotalMismatch => "total differs",
        TicketReconIssue.RefundMismatch => "refunded differs",
        TicketReconIssue.DisputeMismatch => "chargeback differs",
        TicketReconIssue.TicketCountMismatch => "ticket count differs",
        TicketReconIssue.TicketsNotIssued => "tickets not issued",
        _ => issue.ToString(),
    };

    public static string Label(this TicketPaymentIssue issue) => issue switch
    {
        TicketPaymentIssue.NoOrder => "no Humans order",
        TicketPaymentIssue.NotPaidInHumans => "order not paid in Humans",
        TicketPaymentIssue.AmountMismatch => "amount differs from the order",
        TicketPaymentIssue.FeeMismatch => "fee differs from Humans'",
        _ => issue.ToString(),
    };

    public static string Label(this TicketPayoutIssue issue) => issue switch
    {
        TicketPayoutIssue.ContentsMismatch => "amount differs from its contents",
        TicketPayoutIssue.Failed => "failed",
        _ => issue.ToString(),
    };

    public static string Eur(long minor) =>
        (minor / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " €";

    /// <summary>"total differs 2, duplicate 1": each label with how often it occurs.</summary>
    public static string Counted(IEnumerable<string> labels) =>
        string.Join(", ", labels.GroupBy(l => l, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"));
}

/// <summary>One order as both sides see it; either side may be missing.</summary>
internal sealed record TicketReconRow(
    TicketCheckoutOrder? Order,
    BackendOrderSummary? Backend,
    int? TicketsIssued,
    IReadOnlyList<TicketReconIssue> Issues)
{
    public Instant CreatedAt => Order?.CreatedAt ?? Backend!.CreatedAt;

    /// <summary>Paid, refunded or charged back on either side. A difference where no money moved is listed, not out of balance.</summary>
    public bool MoneyMoved =>
        Order is { } o && (o.State == TicketCheckoutOrderState.Paid || o.RefundedMinor > 0 || o.DisputedMinor > 0)
        || Backend is { } b && (b.State == CheckoutState.Paid || b.RefundedMinor > 0 || b.DisputedMinor > 0);
}

/// <summary>
/// <see cref="OtherInstance"/> holds backend orders that name a Humans order this database never held, outside
/// production only: another Humans instance on the same test account made them. They are neither counted nor a difference.
/// </summary>
internal sealed record TicketRecon(
    string BackendKey,
    TicketReconTotals Humans,
    TicketReconTotals Backend,
    int TicketsIssued,
    IReadOnlyList<TicketReconRow> Rows,
    IReadOnlyList<BackendOrderSummary> OtherInstance,
    string? Error)
{
    public IEnumerable<TicketReconRow> Problems => Rows.Where(r => r.Issues.Count > 0);

    public bool Balanced => Error is null && Humans == Backend && Humans.Tickets == TicketsIssued && !Problems.Any(r => r.MoneyMoved);
}

/// <summary>
/// <para>
/// Compares Humans' orders on one backend with the backend's own list for a period. A pair belongs to the period its
/// Humans order was created in; a backend order without one, to the period the backend created it in. The backend is
/// read <see cref="Margin"/> wider on both sides, so an order created on either side of midnight is still paired.
/// </para>
/// <para>
/// Totals count paid orders only: a pending, expired or cancelled order took no money on either side. Each order is
/// still matched, so one that is missing, extra or doubled shows whatever its state; where no money moved on either
/// side (an open checkout a backend does not list, an expired leftover), it is listed and does not unbalance. An order no backend ever took (a
/// free one, paid with nothing to charge) stays out of both sides' totals; only its tickets are checked.
/// </para>
/// </summary>
internal static class TicketReconBuilder
{
    public static readonly Duration Margin = Duration.FromHours(1);

    /// <summary>Reconciles <paramref name="backendKey"/> for [<paramref name="from"/>, <paramref name="to"/>).</summary>
    /// <param name="backendKey">The backend reconciled.</param>
    /// <param name="from">Start of the period, included.</param>
    /// <param name="to">End of the period, excluded.</param>
    /// <param name="orders">Every Humans order on any backend, so an order of another period is told from an extra one.</param>
    /// <param name="backendOrders">The backend's list for the widened period.</param>
    /// <param name="tickets">Tickets issued from the period's orders.</param>
    /// <param name="sharedAccount">
    /// Outside production, where QA, previews and dev share one test account: a backend order naming a Humans order this
    /// database never held is another instance's, not an extra one.
    /// </param>
    public static TicketRecon Build(
        string backendKey,
        Instant from,
        Instant to,
        IReadOnlyCollection<TicketCheckoutOrder> orders,
        IReadOnlyCollection<BackendOrderSummary> backendOrders,
        IReadOnlyCollection<TicketCheckoutTicket> tickets,
        bool sharedAccount = false)
    {
        var known = orders.ToDictionary(o => o.Id);
        var inPeriod = orders
            .Where(o => string.Equals(o.BackendKey, backendKey, StringComparison.Ordinal) && o.CreatedAt >= from && o.CreatedAt < to)
            .ToList();
        var issued = tickets.GroupBy(t => t.OrderId).ToDictionary(g => g.Key, g => g.Select(t => t.LineIndex).Distinct().Count());
        var backend = backendOrders.DistinctBy(b => b.BackendOrderId, StringComparer.Ordinal).ToList();
        var byHumansId = backend.Where(b => b.HumansOrderId is not null)
            .GroupBy(b => b.HumansOrderId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rows = new List<TicketReconRow>();
        var taken = inPeriod.Select(o => o.Id).ToHashSet();
        foreach (var order in inPeriod)
        {
            var mine = byHumansId.GetValueOrDefault(order.Id) ?? [];
            // The one Humans recorded leads; with none recorded, the backend's oldest. A recorded one the backend no
            // longer lists is missing, and whatever it does list for the order was never recorded.
            var primary = order.BackendOrderId is null
                ? mine.OrderBy(b => b.CreatedAt).FirstOrDefault()
                : mine.FirstOrDefault(b => string.Equals(b.BackendOrderId, order.BackendOrderId, StringComparison.Ordinal));
            int? ticketsIssued = order.State == TicketCheckoutOrderState.Paid ? issued.GetValueOrDefault(order.Id) : null;
            if (primary is null && order.BackendOrderId is null
                && (order.State != TicketCheckoutOrderState.Paid || order.TotalMinor == 0))
            {
                // The backend never took it (unopened, or free and paid with nothing to charge): only its tickets to check.
                taken.Remove(order.Id);
                if (ticketsIssued is { } free && free != TicketLines(order))
                    rows.Add(new TicketReconRow(order, null, ticketsIssued, [TicketReconIssue.TicketsNotIssued]));
                continue;
            }

            rows.Add(new TicketReconRow(order, primary, ticketsIssued, Compare(order, primary, ticketsIssued)));
            var other = primary is null ? TicketReconIssue.NotRecorded : TicketReconIssue.Duplicate;
            rows.AddRange(mine.Where(b => b != primary).Select(b => new TicketReconRow(order, b, null, [other])));
        }

        var otherInstance = new List<BackendOrderSummary>();
        foreach (var b in backend.Where(b => b.CreatedAt >= from && b.CreatedAt < to))
        {
            var owner = b.HumansOrderId is { } id ? known.GetValueOrDefault(id) : null;
            if (owner is not null && string.Equals(owner.BackendKey, backendKey, StringComparison.Ordinal))
                continue; // Paired above, or belongs to its order's own period.
            if (sharedAccount && owner is null && b.HumansOrderId is not null)
            {
                otherInstance.Add(b);
                continue;
            }

            rows.Add(new TicketReconRow(owner, b, null, [TicketReconIssue.ExtraAtBackend]));
        }

        var counted = rows.Where(r => r.Backend is not null).Select(r => r.Backend!).DistinctBy(b => b.BackendOrderId, StringComparer.Ordinal);
        var paidHere = inPeriod.Where(o => o.State == TicketCheckoutOrderState.Paid && taken.Contains(o.Id)).ToList();
        return new TicketRecon(
            backendKey,
            new TicketReconTotals(
                paidHere.Count,
                paidHere.Sum(TicketLines),
                paidHere.Sum(o => o.TotalMinor),
                paidHere.Sum(o => o.RefundedMinor),
                paidHere.Sum(o => o.DisputedMinor)),
            Totals(counted.Where(b => b.State == CheckoutState.Paid).ToList(), rows),
            paidHere.Sum(o => issued.GetValueOrDefault(o.Id)),
            rows.OrderBy(r => r.CreatedAt).ToList(),
            otherInstance.OrderBy(b => b.CreatedAt).ToList(),
            null);
    }

    public static TicketRecon Unreadable(string backendKey, Instant from, Instant to, string error)
    {
        var none = new TicketReconTotals(0, 0, 0, 0, 0);
        return new TicketRecon(backendKey, none, none, 0, [], [], error);
    }

    /// <summary>A backend that cannot count tickets is given Humans' count for the order, so it never reads as a gap.</summary>
    private static TicketReconTotals Totals(IReadOnlyList<BackendOrderSummary> paid, IReadOnlyList<TicketReconRow> rows)
    {
        var humansTickets = rows.Where(r => r is { Order: not null, Backend: not null })
            .GroupBy(r => r.Backend!.BackendOrderId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => TicketLines(g.First().Order!), StringComparer.Ordinal);
        return new TicketReconTotals(
            paid.Count,
            paid.Sum(b => b.TicketCount ?? humansTickets.GetValueOrDefault(b.BackendOrderId)),
            paid.Sum(b => b.TotalMinor),
            paid.Sum(b => b.RefundedMinor),
            paid.Sum(b => b.DisputedMinor));
    }

    private static IReadOnlyList<TicketReconIssue> Compare(TicketCheckoutOrder order, BackendOrderSummary? backend, int? ticketsIssued)
    {
        var issues = new List<TicketReconIssue>();
        var humansPaid = order.State == TicketCheckoutOrderState.Paid;
        if (ticketsIssued is { } count && count != TicketLines(order))
            issues.Add(TicketReconIssue.TicketsNotIssued);
        if (backend is null)
        {
            issues.Add(TicketReconIssue.MissingAtBackend);
            return issues;
        }

        if (order.BackendOrderId is null)
            issues.Add(TicketReconIssue.NotRecorded);
        if (humansPaid != (backend.State == CheckoutState.Paid))
            issues.Add(TicketReconIssue.PaidMismatch);
        if (backend.TotalMinor != order.TotalMinor)
            issues.Add(TicketReconIssue.TotalMismatch);
        if (backend.RefundedMinor != order.RefundedMinor)
            issues.Add(TicketReconIssue.RefundMismatch);
        if (backend.DisputedMinor != order.DisputedMinor)
            issues.Add(TicketReconIssue.DisputeMismatch);
        if (backend.TicketCount is { } tickets && tickets != TicketLines(order))
            issues.Add(TicketReconIssue.TicketCountMismatch);
        return issues;
    }

    private static int TicketLines(TicketCheckoutOrder order) => order.Lines.Count(l => l.Kind == CheckoutLineKind.Ticket);
}
