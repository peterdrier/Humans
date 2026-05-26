using Humans.Application.Services.Expenses.Dtos;

namespace Humans.Application.Interfaces.Expenses;

public interface IExpenseReportService : IApplicationService
{
    Task<ExpenseReportDto?> GetAsync(Guid id, CancellationToken ct = default);

    Task<ExpenseDetailViewData> GetDetailViewDataAsync(
        Guid viewerUserId, ExpenseReportDto report, CancellationToken ct = default);

    /// <summary>
    /// Returns the report that owns the given attachment (via its line), with
    /// Lines populated. Returns null if the attachment doesn't belong to any
    /// line or the report is gone. Used by the attachment-streaming endpoint
    /// so visibility is decided by the View authorization handler against the
    /// real owning report, rather than by scanning curated queues that may
    /// drift from the handler's grant scope.
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

    Task<Guid> CreateDraftAsync(
        Guid submitterUserId, Guid budgetCategoryId, string? note,
        CancellationToken ct = default);

    Task UpdateDraftAsync(
        Guid reportId, Guid submitterUserId,
        Guid budgetCategoryId, string? note,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> UpdateDraftWithResultAsync(
        Guid reportId, Guid submitterUserId,
        Guid budgetCategoryId, string? note,
        CancellationToken ct = default);

    Task<Guid> AddLineAsync(
        Guid reportId, Guid submitterUserId,
        string description, decimal amount,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> AddLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        string description, decimal amount,
        CancellationToken ct = default);

    Task UpdateLineAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, string description, decimal amount,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> UpdateLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, string description, decimal amount,
        CancellationToken ct = default);

    Task RemoveLineAsync(
        Guid reportId, Guid submitterUserId, Guid lineId,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> RemoveLineWithResultAsync(
        Guid reportId, Guid submitterUserId, Guid lineId,
        CancellationToken ct = default);

    /// <summary>
    /// Stores the file bytes, creates the attachment row, and links it to the line.
    /// Authorizes: submitter ownership + editable status + line belongs to report.
    /// Returns the new attachment id.
    /// </summary>
    Task<Guid> AttachFileToLineAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, string originalFileName, string contentType,
        Stream content, CancellationToken ct = default);

    Task<ExpenseMutationResult> AttachFileToLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, string originalFileName, string contentType,
        Stream content, CancellationToken ct = default);

    /// <summary>
    /// Removes the file, unlinks the attachment from the line, and deletes the attachment row.
    /// Authorizes: submitter ownership + editable status + line belongs to report.
    /// Idempotent — no-op if the line has no attachment.
    /// </summary>
    Task RemoveAttachmentFromLineAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, CancellationToken ct = default);

    Task<bool> SubmitAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default);

    Task<ExpenseMutationResult> SubmitWithResultAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default);

    Task<bool> WithdrawAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default);


    Task<ExpenseMutationResult> WithdrawWithResultAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default);

    Task<ExpenseIbanSaveResult> SaveSubmitterIbanWithResultAsync(
        Guid submitterUserId, string? iban, CancellationToken ct = default);

    Task<ExpenseIbanViewData> GetSubmitterIbanViewAsync(
        Guid submitterUserId, CancellationToken ct = default);

    Task<bool> CoordinatorEndorseAsync(
        Guid reportId, Guid coordinatorUserId, CancellationToken ct = default);

    Task<ExpenseMutationResult> CoordinatorEndorseWithResultAsync(
        Guid reportId, Guid coordinatorUserId, CancellationToken ct = default);

    Task<bool> CoordinatorRejectAsync(
        Guid reportId, Guid coordinatorUserId, string reason,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> CoordinatorRejectWithResultAsync(
        Guid reportId, Guid coordinatorUserId, string reason,
        CancellationToken ct = default);

    Task<bool> ApproveAsync(
        Guid reportId, Guid actorUserId, Guid? overrideCategoryId,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> ApproveWithResultAsync(
        Guid reportId, Guid actorUserId, Guid? overrideCategoryId,
        CancellationToken ct = default);

    Task<bool> FinanceRejectAsync(
        Guid reportId, Guid actorUserId, string reason,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> FinanceRejectWithResultAsync(
        Guid reportId, Guid actorUserId, string reason,
        CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> MarkSepaSentAsync(
        IReadOnlyCollection<Guid> reportIds, Guid actorUserId,
        CancellationToken ct = default);

    Task<bool> MarkPaidAsync(
        Guid reportId, NodaTime.Instant paidAt, CancellationToken ct = default);

    /// <summary>True iff the category has at least one budget coordinator
    /// (so the Submitted -> CoordinatorEndorsed step is required).</summary>
    Task<bool> CategoryRequiresCoordinatorEndorsementAsync(
        Guid categoryId, CancellationToken ct = default);

    /// <summary>
    /// Drains the Holded expense outbox: creates or updates purchase documents in Holded
    /// for each approved expense report. Called by the recurring job.
    /// </summary>
    Task DrainHoldedOutboxAsync(int batchSize, CancellationToken ct = default);

    /// <summary>
    /// Reconciles payment status on SepaSent expense reports against the member's Holded creditor
    /// balance and marks them Paid when that balance is settled (≥ 0) — treasury pays the creditor
    /// account in aggregate, not per-document. Missing supplier-account numbers are backfilled here.
    /// Called by the recurring job.
    /// </summary>
    Task PollHoldedPaidStatusAsync(int batchSize, CancellationToken ct = default);
}

public sealed record ExpenseAttachmentDownload(
    byte[] Bytes,
    string ContentType,
    string OriginalFileName);

public sealed record ExpenseMutationResult(bool Succeeded, string? ErrorMessage)
{
    public static ExpenseMutationResult Success { get; } = new(true, null);

    public static ExpenseMutationResult Failure(string message) => new(false, message);
}

public sealed record ExpenseIbanSaveResult(
    bool Succeeded,
    bool IsValidationError,
    string Message,
    bool HasIban,
    string? MaskedIban);

public sealed record ExpenseIbanViewData(bool HasIban, string? MaskedIban);

public sealed record ExpenseDetailViewData(
    string CategoryDisplayName,
    bool CanEdit,
    bool CanSubmit,
    bool CanWithdraw,
    bool HasIban,
    string? MaskedIban,
    ExpenseHoldedTimeline? HoldedTimeline);

/// <summary>Round-trip timeline for the submitter, sourced from the Holded creditor balance.</summary>
public sealed record ExpenseHoldedTimeline(
    bool RegisteredInHolded,
    decimal OwedToMember,
    decimal MemberRegisteredTotal,   // sum of this member's registered-but-unpaid ER totals
    decimal OtherAmount,             // max(0, OwedToMember - MemberRegisteredTotal): fronted / adjustments
    bool Paid,
    NodaTime.LocalDate? PaidOn,
    decimal TotalPaid);
