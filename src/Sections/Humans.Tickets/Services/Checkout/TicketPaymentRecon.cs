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
    /// <summary>The payout failed or was cancelled, so the money is back in the account.</summary>
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
/// <paramref name="HumansPaymentFeesMinor"/> the fees Humans recorded on those same orders.
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
    string? Error)
{
    public IEnumerable<TicketPaymentRow> Problems => Rows.Where(r => r.Issues.Count > 0);

    public IEnumerable<TicketPayoutRow> PayoutProblems => Payouts.Where(p => p.Issues.Count > 0);

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
/// the fee Humans recorded. A refund or chargeback must name an order. A payout must send what it contains and must not
/// have failed. A fee on its own (Stripe's own billing, a chargeback fee) names no order and is listed, not a problem.
/// </para>
/// </summary>
internal static class TicketPaymentReconBuilder
{
    public static TicketPaymentRecon Build(
        string backendKey,
        Instant from,
        Instant to,
        PaymentLedger ledger,
        IReadOnlyCollection<TicketCheckoutOrder> orders)
    {
        var mine = orders.Where(o => string.Equals(o.BackendKey, backendKey, StringComparison.Ordinal)).ToDictionary(o => o.Id);
        var rows = new List<TicketPaymentRow>();
        foreach (var entry in ledger.Entries.OrderBy(e => e.CreatedAt))
        {
            var order = entry.HumansOrderId is { } id ? mine.GetValueOrDefault(id) : null;
            rows.Add(new TicketPaymentRow(entry, order, Check(entry, order)));
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
            null);
    }

    public static TicketPaymentRecon Unreadable(string backendKey, Instant from, Instant to, string error) =>
        new(backendKey, from, to, new TicketPaymentTotals(0, 0, 0, 0, 0, 0), 0, 0, [], [], error);

    /// <summary>Money in from a buyer; a failed debit's reversal is a charge too, but negative.</summary>
    private static bool IsPayment(PaymentLedgerEntry entry) => entry is { Kind: PaymentLedgerEntryKind.Charge, AmountMinor: > 0 };

    private static long Sum(IEnumerable<PaymentLedgerEntry> entries, PaymentLedgerEntryKind kind) =>
        entries.Where(e => e.Kind == kind).Sum(e => e.AmountMinor);

    private static IReadOnlyList<TicketPaymentIssue> Check(PaymentLedgerEntry entry, TicketCheckoutOrder? order)
    {
        if (entry.Kind is not (PaymentLedgerEntryKind.Charge or PaymentLedgerEntryKind.Refund or PaymentLedgerEntryKind.Dispute))
            return [];
        if (order is null)
            return [TicketPaymentIssue.NoOrder];
        if (!IsPayment(entry))
            return [];

        var issues = new List<TicketPaymentIssue>();
        if (order.State is not (TicketCheckoutOrderState.Paid or TicketCheckoutOrderState.Processing))
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
