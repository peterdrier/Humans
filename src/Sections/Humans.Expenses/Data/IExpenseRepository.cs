using Humans.Expenses.Contracts;
using Humans.Base.Interfaces.Repositories;
using Humans.Expenses.Domain;

namespace Humans.Expenses.Data;


internal interface IExpenseRepository : IRepository
{
    // Reads — all return fully-populated DTOs (Lines + Attachment metadata always included).
    // EF entities stay inside the repository; callers see only DTOs.
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
    /// (small dataset — no pagination, sum client-side).
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
    /// <summary>Atomically replaces (or clears) a report line's attachment and removes superseded metadata.
    /// Returns whether the line exists and its removed attachment for post-commit file cleanup.</summary>
    Task<(bool Found, ExpenseAttachment? Replaced)> ReplaceLineAttachmentAsync(
        Guid reportId, Guid lineId, ExpenseAttachment? attachment, CancellationToken ct = default);
    /// <summary>
    /// Marks the line declined (with reason and reviewer) or clears the decline, moving the
    /// report's stored <c>Total</c> by the line's amount. Who may decline is the service's call.
    /// Returns false when the report or line does not exist.
    /// </summary>
    Task<bool> SetLineDeclinedAsync(
        Guid reportId, Guid lineId, bool declined, string? reason, Guid? byUserId,
        NodaTime.Instant now, CancellationToken ct = default);
    /// <summary>Unlinks a report's line and deletes its attachment metadata in one save; returns the removed row for post-commit file cleanup.</summary>
    Task<ExpenseAttachment?> RemoveLineAttachmentAsync(Guid reportId, Guid lineId, CancellationToken ct = default);


    /// <summary>
    /// Rewrites the payee IBAN snapshot on an already-submitted report. Which statuses may be
    /// refreshed is the service's call, as with <c>UpdateDraftAsync</c>; this only writes.
    /// Returns false when the report does not exist.
    /// </summary>
    Task<bool> UpdatePayeeIbanAsync(
        Guid reportId, string payeeIban, NodaTime.Instant updatedAt, CancellationToken ct = default);

    /// <summary>A non-null <paramref name="grantStamp"/> rebooks the report to its pre-approval's
    /// account (no category, no year) and writes <paramref name="grantMaxAmount"/> as the cap, both
    /// read from the grant at submit; null leaves the booking and cap as they are.</summary>
    Task<bool> SubmitAsync(
        Guid reportId,
        string payeeName, string payeeIban,
        NodaTime.Instant submittedAt,
        ExpenseBooking? grantStamp = null,
        decimal? grantMaxAmount = null,
        CancellationToken ct = default);

    Task<bool> WithdrawAsync(
        Guid reportId, NodaTime.Instant updatedAt, CancellationToken ct = default);

    /// <summary>Writes <paramref name="maxAmount"/> as the authorized cap; null clears it (a blanked form field).</summary>
    Task<bool> CoordinatorEndorseAsync(
        Guid reportId, Guid actorUserId, decimal? maxAmount,
        NodaTime.Instant endorsedAt, CancellationToken ct = default);

    Task<bool> CoordinatorRejectAsync(
        Guid reportId, Guid actorUserId,
        string reason, NodaTime.Instant rejectedAt, CancellationToken ct = default);

    /// <summary>Writes <paramref name="maxAmount"/> over any cap set at endorsement; null clears it (a blanked form field).
    /// A non-null <paramref name="overrideBooking"/> rebooks the report (account, category and year together).</summary>
    Task<bool> ApproveAsync(
        Guid reportId, Guid actorUserId,
        ExpenseBooking? overrideBooking,
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
    /// Atomically claims the event for drain run <paramref name="runId"/>. Succeeds only while the
    /// event is unprocessed and either unclaimed or claimed before <paramref name="staleBefore"/>
    /// (a crashed run's abandoned claim). Returns false when another run holds it. Released by
    /// <see cref="IncrementOutboxRetryAsync"/>, <see cref="MarkOutboxFailedPermanentlyAsync"/> and
    /// <see cref="RequeueOutboxForReportAsync"/>; processing needs no release.
    /// </summary>
    Task<bool> TryClaimOutboxAsync(
        Guid outboxEventId, Guid runId, NodaTime.Instant now, NodaTime.Instant staleBefore,
        CancellationToken ct = default);
    /// <summary>
    /// The report's most recent outbox event, or null if a push was never queued. Drives the
    /// Holded sync card on <c>/Expenses/{id}</c>.
    /// </summary>
    Task<HoldedExpenseOutboxEvent?> GetLatestOutboxForReportAsync(
        Guid reportId, CancellationToken ct = default);
    /// <summary>Reports with a written-off push — the <c>/Expenses/Review</c> banner and flags.</summary>
    Task<IReadOnlyList<Guid>> GetFailedOutboxReportIdsAsync(CancellationToken ct = default);
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
    /// Persists the freshly-issued Holded document id on the line. Caller invokes
    /// this immediately after <c>IHoldedClient.CreatePurchaseDocumentAsync</c>
    /// returns for that line — that way a transient failure later in the push
    /// (attachment upload, the next line's doc) does not cause the outbox event to
    /// retry the create call and produce a duplicate Holded document. Marking the
    /// outbox event processed is a separate <see cref="MarkOutboxProcessedAsync"/>
    /// call that runs only after every line's create + upload + approve succeeds.
    /// The write is conditional: it only lands while the line has no doc id. Returns false
    /// (leaving the stored id and the report's <c>UpdatedAt</c> untouched) when another push
    /// already recorded one — the caller then owns a duplicate document and must delete it.
    /// </summary>
    Task<bool> SetLineHoldedDocIdAsync(
        Guid lineId, string holdedDocId, NodaTime.Instant updatedAt,
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
