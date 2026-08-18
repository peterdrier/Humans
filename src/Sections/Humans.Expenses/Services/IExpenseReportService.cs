using Humans.Expenses.Contracts;
using Humans.Expenses.Domain;
using Humans.Application.Interfaces;

namespace Humans.Expenses.Services;

internal interface IExpenseReportService : IExpenseReportServiceRead, IApplicationService
{
    Task<Guid> CreateDraftAsync(
        Guid submitterUserId, Guid budgetCategoryId, string? note,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> UpdateDraftWithResultAsync(
        Guid reportId, Guid submitterUserId,
        Guid budgetCategoryId, string? note,
        CancellationToken ct = default);

    /// <summary>
    /// Adds a Receipt or Invoice line, attaching <paramref name="file"/> in the same operation when
    /// one is supplied (validated before the line is created, so a bad upload leaves nothing
    /// half-made). A non-null <paramref name="parentLineId"/> adds a proof row backing that Invoice
    /// line — reviewed with the report but excluded from the total and never pushed to Holded.
    /// Travel line types are rejected (their creation paths were removed).
    /// </summary>
    Task<ExpenseAddLineResult> AddLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        string description, decimal amount,
        ExpenseLineType lineType = ExpenseLineType.Receipt,
        Guid? parentLineId = null,
        ExpenseFileUpload? file = null,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> UpdateLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, string description, decimal amount,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> RemoveLineWithResultAsync(
        Guid reportId, Guid submitterUserId, Guid lineId,
        CancellationToken ct = default);

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

    Task<ExpenseMutationResult> SubmitWithResultAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default);

    Task<ExpenseMutationResult> WithdrawWithResultAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default);

    Task<ExpenseIbanSaveResult> SaveSubmitterIbanWithResultAsync(
        Guid submitterUserId, string? iban, CancellationToken ct = default);

    /// <summary>A non-null <paramref name="maxAmount"/> caps what this report pays out; null leaves it uncapped.</summary>
    Task<ExpenseMutationResult> CoordinatorEndorseWithResultAsync(
        Guid reportId, Guid coordinatorUserId, decimal? maxAmount,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> CoordinatorRejectWithResultAsync(
        Guid reportId, Guid coordinatorUserId, string reason,
        CancellationToken ct = default);

    /// <summary>A non-null <paramref name="maxAmount"/> overrides any cap the coordinator set.</summary>
    Task<ExpenseMutationResult> ApproveWithResultAsync(
        Guid reportId, Guid actorUserId, Guid? overrideCategoryId, decimal? maxAmount,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> FinanceRejectWithResultAsync(
        Guid reportId, Guid actorUserId, string reason,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> AddMileageLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        string origin, string destination, decimal km,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> AddPerDiemLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        PerDiemKind kind, int days, string? note,
        CancellationToken ct = default);

    /// <summary>
    /// Puts a stuck Holded push back in the queue — written off, or waiting out a backoff a finance
    /// admin no longer wants to wait for. Resets the retry budget; the next drain pass picks it up.
    /// Fails when the report has no push in either state.
    /// </summary>
    Task<ExpenseMutationResult> RequeueHoldedPushWithResultAsync(
        Guid reportId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Written-off Holded pushes across all reports — the /Expenses/Review banner count.</summary>
    Task<int> CountFailedHoldedPushesAsync(CancellationToken ct = default);
}

internal sealed record ExpenseMutationResult(bool Succeeded, string? ErrorMessage)
{
    public static ExpenseMutationResult Success { get; } = new(true, null);

    public static ExpenseMutationResult Failure(string message) => new(false, message);
}

/// <summary>Line-add outcome; <see cref="LineId"/> is set on success so the caller can redirect
/// into the new line's flow (an invoice line continues to its proofs page).</summary>
internal sealed record ExpenseAddLineResult(bool Succeeded, string? ErrorMessage, Guid? LineId);

/// <summary>An uploaded file passed through to the service untouched.</summary>
internal sealed record ExpenseFileUpload(string FileName, string ContentType, Stream Content);

internal sealed record ExpenseIbanSaveResult(
    bool Succeeded,
    bool IsValidationError,
    string Message);
