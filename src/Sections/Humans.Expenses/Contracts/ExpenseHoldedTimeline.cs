using NodaTime;

namespace Humans.Expenses.Contracts;

public sealed record ExpenseAttachmentDownload(
    byte[] Bytes,
    string ContentType,
    string OriginalFileName);

/// <summary>
/// The payment half (for the submitter) comes from the cached Holded creditor ledger; the push half (for finance
/// admins) from the report's outbox event.
/// </summary>
public sealed record ExpenseHoldedTimeline
{
    public required bool RegisteredInHolded { get; init; }
    public required decimal OwedToMember { get; init; }
    /// <summary>Sum of this member's registered-but-unpaid report totals.</summary>
    public required decimal MemberRegisteredTotal { get; init; }
    /// <summary>max(0, OwedToMember - MemberRegisteredTotal): fronted amounts and adjustments.</summary>
    public required decimal OtherAmount { get; init; }
    public required bool Paid { get; init; }
    public LocalDate? PaidOn { get; init; }
    public required decimal TotalPaid { get; init; }

    public required ExpenseHoldedSyncState SyncState { get; init; }
    /// <summary>The report's approval. Null when nothing was ever queued.</summary>
    public Instant? QueuedAt { get; init; }
    /// <summary>Succeeded, or written off as permanently failed.</summary>
    public Instant? SettledAt { get; init; }
    /// <summary>Against <see cref="MaxRetries"/>.</summary>
    public int RetryCount { get; init; }
    public int MaxRetries { get; init; }
    /// <summary>From the most recent failed attempt.</summary>
    public string? LastError { get; init; }
    /// <summary>Null unless <see cref="SyncState"/> is Retrying.</summary>
    public Instant? NextRetryAt { get; init; }

    /// <summary>
    /// Waiting out a backoff, or written off. A re-queue resets the retry budget and drains on the next pass.
    /// </summary>
    public bool CanRetry => SyncState is ExpenseHoldedSyncState.Retrying or ExpenseHoldedSyncState.Failed;
}

public enum ExpenseHoldedSyncState
{
    /// <summary>The report is not approved yet.</summary>
    NotQueued,
    /// <summary>Waiting for the next drain pass; nothing has failed.</summary>
    Queued,
    /// <summary>At least one transient failure; waiting out the backoff.</summary>
    Retrying,
    /// <summary>A permanent error, or the retry budget ran out. Needs a re-queue.</summary>
    Failed,
    /// <summary>Purchase document created and attachments uploaded.</summary>
    Pushed,
    /// <summary>No Holded API key is configured, so the drain is not running at all.</summary>
    NotConfigured,
}
