using NodaTime;

namespace Humans.Application.Services.Store.Dtos;

/// <summary>
/// Reconciliation status of a single Stripe Checkout Session against recorded
/// <c>StorePayment</c> rows. See the 2026-06-04 reconciliation design.
/// </summary>
public enum StripeReconciliationStatus
{
    /// <summary>Paid session whose PaymentIntent is already a recorded payment.</summary>
    Recorded,
    /// <summary>
    /// Paid session whose PaymentIntent is recorded but still <c>Pending</c> locally — Stripe has
    /// confirmed the money, the settlement webhook hasn't landed yet. The amount is NOT counted
    /// toward the order balance, so don't read this as "accounted for".
    /// </summary>
    RecordedPending,
    /// <summary>Paid session, order resolved and billable, but not yet recorded — recordable.</summary>
    Missing,
    /// <summary>Paid session with no resolvable billable order (missing metadata, deleted order, or Team order).</summary>
    Unmatched,
    /// <summary>Session not in a paid state (open / expired / async-pending) — informational.</summary>
    Unpaid,
}

/// <summary>One Stripe Checkout Session row in the reconciliation view.</summary>
public sealed record StripeReconciliationRow(
    string SessionId,
    string? PaymentIntentId,
    decimal? AmountEur,
    string? PaymentStatus,
    Instant? CreatedAt,
    Guid? OrderId,
    string? OrderLabel,
    StripeReconciliationStatus Status);

/// <summary>
/// A recorded Stripe-method payment with no matching session in the Stripe list —
/// reported for human review, never auto-deleted.
/// </summary>
public sealed record StripeOrphanPayment(
    string PaymentIntentId,
    Guid OrderId,
    string? OrderLabel,
    decimal AmountEur,
    Instant ReceivedAt);

/// <summary>
/// Full reconciliation report for the Store Stripe Payments admin screen: webhook/checkout
/// health, every Stripe payment matched to its order, and recorded payments orphaned from Stripe.
/// </summary>
/// <param name="StripeQueried">
/// True when the Stripe session list was actually read. False when Stripe could not be queried
/// (key unset or missing read scope) — in which case <see cref="Orphans"/> is empty rather than
/// false-flagging every recorded payment as an orphan.
/// </param>
public sealed record StripeReconciliationReport(
    bool WebhookConfigured,
    bool CheckoutConfigured,
    bool StripeQueried,
    IReadOnlyList<StripeReconciliationRow> Rows,
    IReadOnlyList<StripeOrphanPayment> Orphans)
{
    public int RecordedCount => Rows.Count(r => r.Status == StripeReconciliationStatus.Recorded);
    public int RecordedPendingCount => Rows.Count(r => r.Status == StripeReconciliationStatus.RecordedPending);
    public int MissingCount => Rows.Count(r => r.Status == StripeReconciliationStatus.Missing);
    public int UnmatchedCount => Rows.Count(r => r.Status == StripeReconciliationStatus.Unmatched);

    public decimal MissingTotalEur => Rows
        .Where(r => r.Status == StripeReconciliationStatus.Missing)
        .Sum(r => r.AmountEur ?? 0m);
}

/// <summary>Outcome of recording the missing payments: how many were recorded and their total.</summary>
public sealed record StripeReconciliationResult(int RecordedCount, decimal TotalEur);
