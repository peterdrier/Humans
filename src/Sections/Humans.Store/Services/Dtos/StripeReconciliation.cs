using NodaTime;

namespace Humans.Store.Services.Dtos;

internal enum StripeReconciliationStatus
{
    /// <summary>Recorded as Paid locally.</summary>
    Recorded,
    /// <summary>
    /// Recorded but still Pending locally: Stripe confirmed the money, the settlement webhook hasn't landed. Not counted
    /// toward the balance, so don't read it as accounted for.
    /// </summary>
    RecordedPending,
    /// <summary>Order resolved and billable, but not yet recorded.</summary>
    Missing,
    /// <summary>No resolvable billable order (missing metadata, deleted order, or Team order).</summary>
    Unmatched,
    /// <summary>Not paid (open, expired or async-pending); informational.</summary>
    Unpaid,
    /// <summary>The local payment is Failed: a conflicting terminal outcome needing review.</summary>
    RecordedFailed,
}

internal sealed record StripeReconciliationRow(
    string SessionId,
    string? PaymentIntentId,
    decimal? AmountEur,
    Instant? CreatedAt,
    Guid? OrderId,
    string? OrderLabel,
    StripeReconciliationStatus Status)
{
    public bool RequiresReview => Status is not (StripeReconciliationStatus.Recorded or StripeReconciliationStatus.Unpaid);
}

/// <summary>A recorded Stripe payment with no matching session: reported for review, never auto-deleted.</summary>
internal sealed record StripeOrphanPayment(
    string PaymentIntentId,
    Guid OrderId,
    string? OrderLabel,
    decimal AmountEur,
    Instant ReceivedAt);

/// <summary>
/// <paramref name="StripeQueried"/> is false when Stripe couldn't be read (key unset or missing read scope);
/// <paramref name="Orphans"/> is then empty rather than flagging every recorded payment.
/// </summary>
internal sealed record StripeReconciliationReport(
    bool WebhookConfigured,
    bool CheckoutConfigured,
    bool StripeQueried,
    IReadOnlyList<StripeReconciliationRow> Rows,
    IReadOnlyList<StripeOrphanPayment> Orphans)
{
    public int RecordedCount => Rows.Count(r => r.Status == StripeReconciliationStatus.Recorded);
    public int RecordedFailedCount => Rows.Count(r => r.Status == StripeReconciliationStatus.RecordedFailed);
    public bool IsReconciled => StripeQueried && Orphans.Count == 0 && Rows.All(r => !r.RequiresReview);

    public int RecordedPendingCount => Rows.Count(r => r.Status == StripeReconciliationStatus.RecordedPending);
    public int MissingCount => Rows.Count(r => r.Status == StripeReconciliationStatus.Missing);
    public int UnmatchedCount => Rows.Count(r => r.Status == StripeReconciliationStatus.Unmatched);

    public decimal MissingTotalEur => Rows
        .Where(r => r.Status == StripeReconciliationStatus.Missing)
        .Sum(r => r.AmountEur ?? 0m);
}

internal sealed record StripeReconciliationResult(int RecordedCount, decimal TotalEur);

/// <summary>
/// <paramref name="Refusal"/> is set when nothing could be checked. <paramref name="PendingCount"/> is how many of the
/// newly recorded refunds Stripe is still sending; <paramref name="SettledCount"/> and <paramref name="CancelledCount"/>
/// count earlier-booked refunds that Stripe has since completed or failed.
/// </summary>
internal sealed record StripeRefundCheckResult(
    int RecordedCount, decimal TotalEur, int PendingCount, int SettledCount, int CancelledCount, string? Refusal = null);
