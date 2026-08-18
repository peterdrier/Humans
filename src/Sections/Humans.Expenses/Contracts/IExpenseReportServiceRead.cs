using NodaTime;

namespace Humans.Expenses.Contracts;

/// <summary>
/// Cross-section read surface for expense reports. External readers should use
/// this interface instead of the mutation service.
/// </summary>
public interface IExpenseReportServiceRead
{
    Task<ExpenseReportDto?> GetAsync(Guid id, CancellationToken ct = default);

    Task<ExpenseHoldedTimeline?> GetHoldedTimelineAsync(
        ExpenseReportDto report, CancellationToken ct = default);

    /// <summary>
    /// Returns the report that owns the given attachment (via its line), with
    /// Lines populated. Returns null if the attachment doesn't belong to any
    /// line or the report is gone.
    /// </summary>
    Task<ExpenseReportDto?> GetReportOwningAttachmentAsync(
        Guid attachmentId, CancellationToken ct = default);

    Task<ExpenseAttachmentDownload?> TryReadAttachmentAsync(
        ExpenseReportDto owningReport,
        Guid attachmentId,
        CancellationToken ct = default);

    Task<IReadOnlyList<ExpenseReportDto>> GetForSubmitterAsync(
        Guid submitterUserId, CancellationToken ct = default);

    Task<IReadOnlyList<ExpenseReportDto>> GetCoordinatorQueueAsync(
        Guid coordinatorUserId, CancellationToken ct = default);

    Task<IReadOnlyList<ExpenseReportDto>> GetReviewQueueAsync(CancellationToken ct = default);

    /// <summary>All expense reports, all statuses — dashboard/aggregate reads sum client-side (~500-user scale).</summary>
    Task<IReadOnlyList<ExpenseReportDto>> GetAllAsync(CancellationToken ct = default);
}

public sealed record ExpenseAttachmentDownload(
    byte[] Bytes,
    string ContentType,
    string OriginalFileName);

/// <summary>
/// Round-trip timeline for a single report: the submitter-facing payment half, derived from the
/// cached Holded creditor ledger, plus the finance-admin-facing push half, derived from the report's
/// outbox event. The push half answers "did this one reach Holded?" — the question that used to be
/// answerable only from server logs (nobodies-collective/Humans#1045).
/// </summary>
public sealed record ExpenseHoldedTimeline
{
    // Payment half — creditor ledger.
    public required bool RegisteredInHolded { get; init; }
    public required decimal OwedToMember { get; init; }
    /// <summary>Sum of this member's registered-but-unpaid ER totals.</summary>
    public required decimal MemberRegisteredTotal { get; init; }
    /// <summary>max(0, OwedToMember - MemberRegisteredTotal): fronted / adjustments.</summary>
    public required decimal OtherAmount { get; init; }
    public required bool Paid { get; init; }
    public LocalDate? PaidOn { get; init; }
    public required decimal TotalPaid { get; init; }

    // Push half — outbox event.
    public required ExpenseHoldedSyncState SyncState { get; init; }
    /// <summary>When the push was queued (the report's approval). Null when nothing was ever queued.</summary>
    public Instant? QueuedAt { get; init; }
    /// <summary>When the push finished — succeeded or was written off as permanently failed.</summary>
    public Instant? SettledAt { get; init; }
    /// <summary>Failed attempts so far, against <see cref="MaxRetries"/>.</summary>
    public int RetryCount { get; init; }
    public int MaxRetries { get; init; }
    /// <summary>Error from the most recent failed attempt; null when there has not been one.</summary>
    public string? LastError { get; init; }
    /// <summary>Earliest instant the drain will try again. Null unless <see cref="SyncState"/> is Retrying.</summary>
    public Instant? NextRetryAt { get; init; }

    /// <summary>
    /// True when a finance admin can re-queue the push: it is waiting out a backoff, or it was
    /// written off. Re-queuing resets the retry budget and drains on the next pass.
    /// </summary>
    public bool CanRetry => SyncState is ExpenseHoldedSyncState.Retrying or ExpenseHoldedSyncState.Failed;
}

/// <summary>Where a report's Holded push currently stands. Replaces the old blank-or-not rendering
/// of <c>HoldedDocId is null</c>, which collapsed all of these into "nothing".</summary>
public enum ExpenseHoldedSyncState
{
    /// <summary>No push has been queued — the report is not approved yet.</summary>
    NotQueued,
    /// <summary>Queued and waiting for the next drain pass; nothing has failed.</summary>
    Queued,
    /// <summary>At least one attempt failed transiently; waiting out the backoff.</summary>
    Retrying,
    /// <summary>Written off: a permanent error, or the retry budget ran out. Needs a re-queue.</summary>
    Failed,
    /// <summary>Reached Holded — purchase document created and attachments uploaded.</summary>
    Pushed,
    /// <summary>Queued, but no Holded API key is configured, so the drain is not running at all.</summary>
    NotConfigured,
}
