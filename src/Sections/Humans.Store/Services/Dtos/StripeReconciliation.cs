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
    string? PaymentStatus,
    Instant? CreatedAt,
    Guid? OrderId,
    string? OrderLabel,
    StripeReconciliationStatus Status);

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
    public int RecordedPendingCount => Rows.Count(r => r.Status == StripeReconciliationStatus.RecordedPending);
    public int MissingCount => Rows.Count(r => r.Status == StripeReconciliationStatus.Missing);
    public int UnmatchedCount => Rows.Count(r => r.Status == StripeReconciliationStatus.Unmatched);

    public decimal MissingTotalEur => Rows
        .Where(r => r.Status == StripeReconciliationStatus.Missing)
        .Sum(r => r.AmountEur ?? 0m);
}

internal sealed record StripeReconciliationResult(int RecordedCount, decimal TotalEur);
