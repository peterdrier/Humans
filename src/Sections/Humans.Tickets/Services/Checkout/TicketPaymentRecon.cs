using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using NodaTime;

namespace Humans.Tickets.Services.Checkout;

internal enum TicketPaymentIssue
{
    /// <summary>A payment, refund or chargeback that names no Humans order on this backend.</summary>
    NoOrder,
    /// <summary>A payment for an order Humans holds as neither paid nor settling.</summary>
    NotPaidInHumans,
    /// <summary>A payment of another amount than the order's total.</summary>
    AmountMismatch,
    /// <summary>The fee the provider kept on the payment is not the fee Humans recorded on the order.</summary>
    FeeMismatch,
}

internal enum TicketPayoutIssue
{
    /// <summary>The payout sends another amount than the net of what the provider says it contains.</summary>
    ContentsMismatch,
    /// <summary>
    /// The payout failed or was cancelled, so the money is back in the account and goes out again in a later payout.
    /// Shown, but it never unbalances: a failed payout stays failed, so it would hold the nightly alert forever.
    /// </summary>
    Failed,
}

/// <summary>One balance movement, with the Humans order it names when there is one.</summary>
internal sealed record TicketPaymentRow(PaymentLedgerEntry Entry, TicketCheckoutOrder? Order, IReadOnlyList<TicketPaymentIssue> Issues);

internal sealed record TicketPayoutRow(PaymentPayout Payout, IReadOnlyList<TicketPayoutIssue> Issues);

/// <summary>
/// What moved through the payment account in the period. Refunds, chargebacks, fees and payouts are positive amounts
/// out; <see cref="OtherMinor"/> is signed. <see cref="NetMinor"/> is what stayed in the balance before payouts.
/// </summary>
internal sealed record TicketPaymentTotals(
    long ChargedMinor,
    long RefundedMinor,
    long DisputedMinor,
    long FeesMinor,
    long OtherMinor,
    long PaidOutMinor)
{
    public long NetMinor => ChargedMinor - RefundedMinor - DisputedMinor - FeesMinor + OtherMinor;
}

/// <summary>
/// <paramref name="PaymentFeesMinor"/> is the provider's fees on payments tied to a Humans order;
/// <paramref name="HumansPaymentFeesMinor"/> the fees Humans recorded on those same orders. <paramref name="OtherInstance"/>
/// holds movements naming a Humans order this database never held, outside production only: another Humans instance
/// on the same test account made them. They count in the account's totals and are never a difference.
/// </summary>
internal sealed record TicketPaymentRecon(
    string BackendKey,
    Instant From,
    Instant To,
    TicketPaymentTotals Totals,
    long PaymentFeesMinor,
    long HumansPaymentFeesMinor,
    IReadOnlyList<TicketPaymentRow> Rows,
    IReadOnlyList<TicketPayoutRow> Payouts,
    IReadOnlyList<PaymentLedgerEntry> OtherInstance,
    string? Error)
{
    public IEnumerable<TicketPaymentRow> Problems => Rows.Where(r => r.Issues.Count > 0);

    public IEnumerable<TicketPayoutRow> PayoutProblems => Payouts.Where(p => p.Issues.Any(i => i != TicketPayoutIssue.Failed));

    /// <summary>Fees charged on their own or on a chargeback rather than on a payment, listed for the team to check.</summary>
    public IEnumerable<TicketPaymentRow> OtherFees => Rows.Where(r =>
        r.Entry.Kind is PaymentLedgerEntryKind.Fee or PaymentLedgerEntryKind.Dispute or PaymentLedgerEntryKind.Other
        && (r.Entry.FeeMinor != 0 || r.Entry.Kind == PaymentLedgerEntryKind.Fee));

    public bool Balanced => Error is null && !Problems.Any() && !PayoutProblems.Any();
}

/// <summary>
/// <para>
/// Fees and payouts, from the payment account's side: every balance movement in the period is checked against the
/// Humans order it names, and every payout against what it contains. Unlike the order reconciliation this goes by the
/// day the money moved, so a refund months after the sale counts in the period it was made.
/// </para>
/// <para>
/// A payment must name an order on this backend that Humans holds as paid (or settling), for the order's total, with
/// the fee Humans recorded (a failed delayed debit, whose payment came back, may be Failed). A refund or chargeback must name an order. A payout must send what it contains; a failed
/// one is shown, not counted, since it stays failed forever. A fee on its own (Stripe's own billing, a chargeback fee) names no order and is listed, not a problem.
/// </para>
/// </summary>
internal static class TicketPaymentReconBuilder
{
    /// <summary>
    /// Checks the period's ledger against the orders. <paramref name="returnedLater"/> holds orders whose failed delayed
    /// debit came back after the period, so a period that holds only the payment balances. With
    /// <paramref name="sharedAccount"/> (outside production) a movement naming a Humans order this database never held is
    /// another instance's, set apart rather than a difference.
    /// </summary>
    public static TicketPaymentRecon Build(
        string backendKey,
        Instant from,
        Instant to,
        PaymentLedger ledger,
        IReadOnlyCollection<TicketCheckoutOrder> orders,
        IReadOnlyCollection<Guid>? returnedLater = null,
        bool sharedAccount = false)
    {
        var mine = Mine(orders, backendKey);
        var known = orders.Select(o => o.Id).ToHashSet();
        var returned = Returned(ledger);
        returned.UnionWith(returnedLater ?? []);
        var rows = new List<TicketPaymentRow>();
        var otherInstance = new List<PaymentLedgerEntry>();
        foreach (var entry in ledger.Entries.OrderBy(e => e.CreatedAt))
        {
            if (sharedAccount && entry.HumansOrderId is { } named && !known.Contains(named))
            {
                otherInstance.Add(entry);
                continue;
            }

            var order = entry.HumansOrderId is { } id ? mine.GetValueOrDefault(id) : null;
            rows.Add(new TicketPaymentRow(entry, order, Check(entry, order, order is not null && returned.Contains(order.Id))));
        }

        var payments = rows.Where(r => IsPayment(r.Entry) && r.Order is not null).ToList();
        var entries = ledger.Entries;
        return new TicketPaymentRecon(
            backendKey,
            from,
            to,
            new TicketPaymentTotals(
                Sum(entries, PaymentLedgerEntryKind.Charge),
                -Sum(entries, PaymentLedgerEntryKind.Refund),
                -Sum(entries, PaymentLedgerEntryKind.Dispute),
                entries.Sum(e => e.FeeMinor) - Sum(entries, PaymentLedgerEntryKind.Fee),
                Sum(entries, PaymentLedgerEntryKind.Other),
                -Sum(entries, PaymentLedgerEntryKind.Payout)),
            payments.Sum(r => r.Entry.FeeMinor),
            payments.Sum(r => r.Order!.FeeMinor ?? 0),
            rows,
            ledger.Payouts.OrderBy(p => p.CreatedAt).Select(p => new TicketPayoutRow(p, Check(p))).ToList(),
            otherInstance,
            null);
    }

    public static TicketPaymentRecon Unreadable(string backendKey, Instant from, Instant to, string error) =>
        new(backendKey, from, to, new TicketPaymentTotals(0, 0, 0, 0, 0, 0), 0, 0, [], [], [], error);

    /// <summary>
    /// Whether a payment in the ledger is on a failed order whose return is not in it, so the return must be looked for
    /// after the period.
    /// </summary>
    public static bool AwaitsReturn(PaymentLedger ledger, IReadOnlyCollection<TicketCheckoutOrder> orders, string backendKey)
    {
        var mine = Mine(orders, backendKey);
        var returned = Returned(ledger);
        return ledger.Entries.Any(e => IsPayment(e) && e.HumansOrderId is { } id && !returned.Contains(id)
            && mine.TryGetValue(id, out var order) && order.State == TicketCheckoutOrderState.Failed);
    }

    /// <summary>The orders whose payment came back: a delayed debit that failed shows as its payment and then this.</summary>
    public static HashSet<Guid> Returned(PaymentLedger ledger) => ledger.Entries
        .Where(e => e is { Kind: PaymentLedgerEntryKind.Charge, AmountMinor: < 0, HumansOrderId: not null })
        .Select(e => e.HumansOrderId!.Value).ToHashSet();

    private static Dictionary<Guid, TicketCheckoutOrder> Mine(IReadOnlyCollection<TicketCheckoutOrder> orders, string backendKey) =>
        orders.Where(o => string.Equals(o.BackendKey, backendKey, StringComparison.Ordinal)).ToDictionary(o => o.Id);

    /// <summary>Money in from a buyer; a failed debit's reversal is a charge too, but negative.</summary>
    private static bool IsPayment(PaymentLedgerEntry entry) => entry is { Kind: PaymentLedgerEntryKind.Charge, AmountMinor: > 0 };

    private static long Sum(IEnumerable<PaymentLedgerEntry> entries, PaymentLedgerEntryKind kind) =>
        entries.Where(e => e.Kind == kind).Sum(e => e.AmountMinor);

    private static IReadOnlyList<TicketPaymentIssue> Check(PaymentLedgerEntry entry, TicketCheckoutOrder? order, bool returned)
    {
        if (entry.Kind is not (PaymentLedgerEntryKind.Charge or PaymentLedgerEntryKind.Refund or PaymentLedgerEntryKind.Dispute))
            return [];
        if (order is null)
            return [TicketPaymentIssue.NoOrder];
        if (!IsPayment(entry))
            return [];

        var issues = new List<TicketPaymentIssue>();
        if (order.State is not (TicketCheckoutOrderState.Paid or TicketCheckoutOrderState.Processing)
            && !(order.State == TicketCheckoutOrderState.Failed && returned))
            issues.Add(TicketPaymentIssue.NotPaidInHumans);
        if (entry.AmountMinor != order.TotalMinor)
            issues.Add(TicketPaymentIssue.AmountMismatch);
        if (entry.FeeMinor != order.FeeMinor)
            issues.Add(TicketPaymentIssue.FeeMismatch);
        return issues;
    }

    private static IReadOnlyList<TicketPayoutIssue> Check(PaymentPayout payout)
    {
        var issues = new List<TicketPayoutIssue>();
        if (payout.ContentsNetMinor is { } net && net != payout.AmountMinor)
            issues.Add(TicketPayoutIssue.ContentsMismatch);
        if (payout.Status is "failed" or "canceled")
            issues.Add(TicketPayoutIssue.Failed);
        return issues;
    }
}
