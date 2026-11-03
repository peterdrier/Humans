using Humans.Expenses.Contracts;
using Humans.Base.Interfaces.Repositories;
using Humans.Expenses.Domain;

namespace Humans.Expenses.Data;


internal interface IExpenseRepository : IRepository
{
    // Reads return DTOs with lines and attachment metadata.
    Task<ExpenseReportDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReportDto>> GetForSubmitterAsync(
        Guid submitterUserId, CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReportDto>> GetByCategoryIdsAndStatusAsync(
        IReadOnlyCollection<Guid> categoryIds,
        ExpenseReportStatus status,
        CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReportDto>> GetForReviewQueueAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseReportDto>> GetAllAsync(CancellationToken ct = default);
    /// <summary>Null when no line points at the attachment.</summary>
    Task<Guid?> GetReportIdByAttachmentIdAsync(Guid attachmentId, CancellationToken ct = default);

    // Each write is atomic in its own context.
    Task AddDraftAsync(ExpenseReport report, CancellationToken ct = default);
    Task UpdateDraftAsync(ExpenseReport report, CancellationToken ct = default);
    Task<bool> AddLineAsync(
        Guid reportId, ExpenseLine line, CancellationToken ct = default);
    Task<bool> UpdateLineAsync(
        Guid reportId, ExpenseLine line, CancellationToken ct = default);
    /// <summary>
    /// Removes the line, its proof rows and their attachment rows in one save: a proof added concurrently fails the
    /// whole removal. Returns the removed attachments for post-commit file deletion; null when report or line is missing.
    /// </summary>
    Task<IReadOnlyList<ExpenseAttachment>?> RemoveLineAsync(
        Guid reportId, Guid lineId, CancellationToken ct = default);
    /// <summary>Atomic, including an optional decline-state and total change. Returns whether the line exists
    /// and the replaced attachment for post-commit file cleanup.</summary>
    Task<(bool Found, ExpenseAttachment? Replaced)> ReplaceLineAttachmentAsync(
        Guid reportId, Guid lineId, ExpenseAttachment? attachment, CancellationToken ct = default,
        (NodaTime.Instant UpdatedAt, string? Reason, Guid? ByUserId, NodaTime.Instant? At)? declineState = null);
    /// <summary>
    /// Moves the stored <c>Total</c> by the line's amount. Who may decline is the service's call. False when the report
    /// or line does not exist.
    /// </summary>
    Task<bool> SetLineDeclinedAsync(
        Guid reportId, Guid lineId, bool declined, string? reason, Guid? byUserId,
        NodaTime.Instant now, CancellationToken ct = default);
    /// <summary>One save; returns the removed row for post-commit file cleanup.</summary>
    Task<ExpenseAttachment?> RemoveLineAttachmentAsync(Guid reportId, Guid lineId, CancellationToken ct = default);


    /// <summary>Which statuses may be refreshed is the service's call; this only writes. False when missing.</summary>
    Task<bool> UpdatePayeeIbanAsync(
        Guid reportId, string payeeIban, NodaTime.Instant updatedAt, CancellationToken ct = default);

    /// <summary>A non-null <paramref name="grantStamp"/> rebooks to the pre-approval's account (no category or year)
    /// with <paramref name="grantMaxAmount"/> as the cap; null leaves booking and cap alone.</summary>
    Task<bool> SubmitAsync(
        Guid reportId,
        string payeeName, string payeeIban,
        NodaTime.Instant submittedAt,
        ExpenseBooking? grantStamp = null,
        decimal? grantMaxAmount = null,
        CancellationToken ct = default);

    Task<bool> WithdrawAsync(
        Guid reportId, NodaTime.Instant updatedAt, CancellationToken ct = default);

    /// <summary>Null clears the cap (a blanked form field).</summary>
    Task<bool> CoordinatorEndorseAsync(
        Guid reportId, Guid actorUserId, decimal? maxAmount,
        NodaTime.Instant endorsedAt, CancellationToken ct = default);

    Task<bool> CoordinatorRejectAsync(
        Guid reportId, Guid actorUserId,
        string reason, NodaTime.Instant rejectedAt, CancellationToken ct = default);

    /// <summary>Writes over any endorsement cap; null clears it. A non-null <paramref name="overrideBooking"/> rebooks
    /// account, category and year together.</summary>
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

    /// <summary>Unprocessed, not written off, and past its backoff.</summary>
    Task<IReadOnlyList<HoldedExpenseOutboxEvent>> GetUnprocessedOutboxAsync(
        NodaTime.Instant now, int limit, CancellationToken ct = default);
    /// <summary>Null if a push was never queued.</summary>
    Task<HoldedExpenseOutboxEvent?> GetLatestOutboxForReportAsync(
        Guid reportId, CancellationToken ct = default);
    /// <summary><see cref="GetLatestOutboxForReportAsync"/> for every report that has an event, in one query.</summary>
    Task<IReadOnlyDictionary<Guid, HoldedExpenseOutboxEvent>> GetLatestOutboxByReportAsync(
        CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetFailedOutboxReportIdsAsync(CancellationToken ct = default);
    /// <summary>
    /// Clears the write-off, error, backoff and retry budget. False when the report has no failed or backing-off event;
    /// a healthy report is a no-op.
    /// </summary>
    Task<bool> RequeueOutboxForReportAsync(Guid reportId, CancellationToken ct = default);
    /// <summary>So a re-run skips the file instead of uploading a second copy.</summary>
    Task MarkAttachmentPushedAsync(
        Guid attachmentId, NodaTime.Instant pushedAt, CancellationToken ct = default);
    /// <summary>
    /// Call immediately after the line's doc is created, so a later failure in the push does not retry the create and
    /// duplicate the doc. Lands only while the line has no doc id: false (nothing touched) when another push already
    /// recorded one, and the caller then owns an orphan document to remove in Holded by hand.
    /// </summary>
    Task<bool> SetLineHoldedDocIdAsync(
        Guid lineId, string holdedDocId, NodaTime.Instant updatedAt,
        CancellationToken ct = default);
    /// <summary>A null <paramref name="supplierAccountNum"/> keeps any existing number: it is resolved after doc
    /// creation.</summary>
    Task SetHoldedContactLinkAsync(
        Guid reportId, string holdedContactId, int? supplierAccountNum,
        NodaTime.Instant updatedAt, CancellationToken ct = default);
    /// <summary>Holds the event until <paramref name="nextRetryAt"/>.</summary>
    Task IncrementOutboxRetryAsync(
        Guid outboxEventId, string error, NodaTime.Instant nextRetryAt,
        CancellationToken ct = default);
    Task MarkOutboxFailedPermanentlyAsync(
        Guid outboxEventId, string error,
        NodaTime.Instant processedAt, CancellationToken ct = default);
    Task MarkOutboxProcessedAsync(
        Guid outboxEventId, NodaTime.Instant processedAt, CancellationToken ct = default);
}
