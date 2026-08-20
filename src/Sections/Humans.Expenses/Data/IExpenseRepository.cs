using Humans.Expenses.Contracts;
using Humans.Base.Interfaces.Repositories;
using Humans.Expenses.Domain;

namespace Humans.Expenses.Data;


internal interface IExpenseRepository : IRepository
{
    // Reads — all return fully-populated DTOs (Lines + Attachment metadata always included).
    // EF entity types stay inside Infrastructure; the Application layer sees only DTOs.
    Task<ExpenseReportDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReportDto>> GetForSubmitterAsync(
        Guid submitterUserId, CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReportDto>> GetByCategoryIdsAndStatusAsync(
        IReadOnlyCollection<Guid> categoryIds,
        ExpenseReportStatus status,
        CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReportDto>> GetForReviewQueueAsync(CancellationToken ct = default);
    /// <summary>
    /// Returns every expense report regardless of status. Dashboard/aggregate reads only
    /// (~500-user scale — no pagination, sum client-side).
    /// </summary>
    Task<IReadOnlyList<ExpenseReportDto>> GetAllAsync(CancellationToken ct = default);
    /// <summary>
    /// Resolves the report id that owns the given attachment via the line that
    /// references it. Returns null if no line currently points at the attachment
    /// (orphan attachment or unknown id).
    /// </summary>
    Task<Guid?> GetReportIdByAttachmentIdAsync(Guid attachmentId, CancellationToken ct = default);

    // Writes — atomic per-method, all inside one short-lived DbContext.
    Task AddDraftAsync(ExpenseReport report, CancellationToken ct = default);
    Task UpdateDraftAsync(ExpenseReport report, CancellationToken ct = default);
    Task<bool> AddLineAsync(
        Guid reportId, ExpenseLine line, CancellationToken ct = default);
    Task<bool> UpdateLineAsync(
        Guid reportId, ExpenseLine line, CancellationToken ct = default);
    /// <summary>
    /// Removes the line, any proof rows under it, and every attachment row they reference, in one
    /// atomic save — a proof added concurrently makes the whole removal fail rather than leaving
    /// half the rows gone. Returns the removed attachments so the caller can delete their files
    /// (after the commit), or null when the report or line does not exist.
    /// </summary>
    Task<IReadOnlyList<ExpenseAttachment>?> RemoveLineAsync(
        Guid reportId, Guid lineId, CancellationToken ct = default);
    Task<Guid> AddAttachmentAsync(
        ExpenseAttachment attachment, CancellationToken ct = default);
    Task RemoveAttachmentAsync(Guid id, CancellationToken ct = default);
    Task SetLineAttachmentAsync(
        Guid lineId, Guid? attachmentId, CancellationToken ct = default);

    Task<bool> SubmitAsync(
        Guid reportId,
        string payeeName, string payeeIban,
        NodaTime.Instant submittedAt,
        CancellationToken ct = default);

    Task<bool> WithdrawAsync(
        Guid reportId, NodaTime.Instant updatedAt, CancellationToken ct = default);

    /// <summary>A non-null <paramref name="maxAmount"/> sets the authorized cap; null leaves it as it is.</summary>
    Task<bool> CoordinatorEndorseAsync(
        Guid reportId, Guid actorUserId, decimal? maxAmount,
        NodaTime.Instant endorsedAt, CancellationToken ct = default);

    Task<bool> CoordinatorRejectAsync(
        Guid reportId, Guid actorUserId,
        string reason, NodaTime.Instant rejectedAt, CancellationToken ct = default);

    /// <summary>A non-null <paramref name="maxAmount"/> overrides any cap set at endorsement; null leaves it as it is.</summary>
    Task<bool> ApproveAsync(
        Guid reportId, Guid actorUserId,
        Guid? overrideCategoryId,
        decimal? maxAmount,
        NodaTime.Instant approvedAt,
        Guid outboxEventId,
        CancellationToken ct = default);

    Task<bool> FinanceRejectAsync(
        Guid reportId, Guid actorUserId,
        string reason, NodaTime.Instant rejectedAt, CancellationToken ct = default);

    // Outbox
    /// <summary>
    /// The drain batch: unprocessed, not written off, and past its backoff. Events still inside
    /// their <c>NextRetryAt</c> window are held back rather than re-hitting Holded every minute.
    /// </summary>
    Task<IReadOnlyList<HoldedExpenseOutboxEvent>> GetUnprocessedOutboxAsync(
        NodaTime.Instant now, int limit, CancellationToken ct = default);
    /// <summary>
    /// The report's most recent outbox event, or null if a push was never queued. Drives the
    /// Holded sync card on <c>/Expenses/{id}</c>.
    /// </summary>
    Task<HoldedExpenseOutboxEvent?> GetLatestOutboxForReportAsync(
        Guid reportId, CancellationToken ct = default);
    /// <summary>Written-off events across all reports — the <c>/Expenses/Review</c> banner count.</summary>
    Task<int> CountFailedOutboxAsync(CancellationToken ct = default);
    /// <summary>
    /// Puts the report's failed or backing-off events back at the front of the drain: clears the
    /// write-off, the error, the backoff, and the retry budget. Returns false when the report has
    /// no event in either state. Idempotent — a healthy or already-pushed report is a no-op.
    /// </summary>
    Task<bool> RequeueOutboxForReportAsync(Guid reportId, CancellationToken ct = default);
    /// <summary>
    /// Records that the file reached the report's Holded document, so a later re-run skips it
    /// instead of uploading a second copy to the same doc.
    /// </summary>
    Task MarkAttachmentPushedAsync(
        Guid attachmentId, NodaTime.Instant pushedAt, CancellationToken ct = default);
    /// <summary>
    /// Persists the freshly-issued Holded document id on the report. Caller
    /// invokes this immediately after <c>IHoldedClient.CreatePurchaseDocumentAsync</c>
    /// returns — that way a transient failure during attachment upload (which runs
    /// after) does not cause the outbox event to retry the create call and produce
    /// a duplicate Holded document. Marking the outbox event processed is a
    /// separate <see cref="MarkOutboxProcessedAsync"/> call that runs only after
    /// the full create + upload chain succeeds.
    /// </summary>
    Task SetHoldedDocIdAsync(
        Guid reportId, string holdedDocId, NodaTime.Instant updatedAt,
        CancellationToken ct = default);
    /// <summary>
    /// Persists the Holded contact id and (optionally) the resolved 400000xx supplier-account
    /// number on the report. A null <paramref name="supplierAccountNum"/> leaves any existing
    /// number untouched (it is resolved post-doc-creation and may not exist on the first call).
    /// </summary>
    Task SetHoldedContactLinkAsync(
        Guid reportId, string holdedContactId, int? supplierAccountNum,
        NodaTime.Instant updatedAt, CancellationToken ct = default);
    /// <summary>Records a transient failure and holds the event until <paramref name="nextRetryAt"/>.</summary>
    Task IncrementOutboxRetryAsync(
        Guid outboxEventId, string error, NodaTime.Instant nextRetryAt,
        CancellationToken ct = default);
    Task MarkOutboxFailedPermanentlyAsync(
        Guid outboxEventId, string error,
        NodaTime.Instant processedAt, CancellationToken ct = default);
    Task MarkOutboxProcessedAsync(
        Guid outboxEventId, NodaTime.Instant processedAt, CancellationToken ct = default);
}
