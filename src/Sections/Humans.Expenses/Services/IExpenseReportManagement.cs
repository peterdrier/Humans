using Humans.Expenses.Contracts;
using Humans.Expenses.Domain;
using Humans.Base.Interfaces;

namespace Humans.Expenses.Services;

/// <summary>Expense report reads and actor-aware mutations.</summary>
/// <remarks>
/// <para><b>Actor vs submitter.</b> A report belongs to its <c>SubmitterUserId</c>; the actor is
/// whoever is currently changing it. They are the same person for a member working on their own
/// report, and differ when a finance admin files or fixes one on a member's behalf. Every mutation
/// therefore takes <c>actorUserId</c> plus <c>actorIsFinanceAdmin</c> — the caller's authority,
/// resolved at the controller (design-rules §6), mirroring
/// <see cref="IExpenseReportService.GetReviewQueueAsync"/>. Set it and the ownership match is
/// waived and the editable window widens from Draft to Draft/Submitted/CoordinatorEndorsed; the
/// checks are defence in depth behind the resource-based handler, not the primary gate.</para>
/// </remarks>
internal interface IExpenseReportManagement : IExpenseReportService, IApplicationService
{
    Task<IReadOnlyList<ExpenseReportDto>> GetForSubmitterAsync(
        Guid submitterUserId, CancellationToken ct = default);

    Task<IReadOnlyList<ExpenseReportDto>> GetCoordinatorQueueAsync(
        Guid coordinatorUserId, CancellationToken ct = default);

    /// <summary>All expense reports, all statuses — dashboard/aggregate reads sum client-side (small dataset).</summary>
    Task<IReadOnlyList<ExpenseReportDto>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Creates a draft owned by <paramref name="submitterUserId"/>, booked to the Holded
    /// expense account <paramref name="holdedAccountNumber"/> (its budget category, when it has one,
    /// is derived from Finance's map). A non-null <paramref name="preapprovalGrantId"/> files the
    /// report against that pre-approval instead: it must be claimable by the submitter, and the
    /// account comes from the grant, with no budget category. When <paramref name="actorUserId"/>
    /// is someone else, the creation is audited as on-behalf.</summary>
    Task<ExpenseCreateDraftResult> CreateDraftAsync(
        Guid submitterUserId, Guid actorUserId, int holdedAccountNumber, string? note,
        Guid? preapprovalGrantId = null,
        CancellationToken ct = default);

    /// <summary>On a Draft, <paramref name="preapprovalGrantId"/> may keep, swap or drop the
    /// pre-approval the report files against; past Draft the pre-approval and its account are locked
    /// and only the note changes.</summary>
    Task<ExpenseMutationResult> UpdateDraftWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin,
        int holdedAccountNumber, string? note,
        Guid? preapprovalGrantId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Adds a Receipt or Invoice line, attaching <paramref name="file"/> in the same operation when
    /// one is supplied (validated before the line is created, so a bad upload leaves nothing
    /// half-made). A non-null <paramref name="parentLineId"/> adds a proof row backing that Invoice
    /// line — reviewed with the report but excluded from the total and never pushed to Holded.
    /// Travel line types are rejected: their amounts are computed, so they go through the
    /// Mileage/PerDiem add methods.
    /// </summary>
    Task<ExpenseAddLineResult> AddLineWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin,
        string description, decimal amount,
        ExpenseLineType lineType = ExpenseLineType.Receipt,
        Guid? parentLineId = null,
        ExpenseFileUpload? file = null,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> UpdateLineWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin,
        Guid lineId, string description, decimal amount,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> RemoveLineWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin, Guid lineId,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> AttachFileToLineWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin,
        Guid lineId, string originalFileName, string contentType,
        Stream content, CancellationToken ct = default);

    /// <summary>
    /// Removes the file, unlinks the attachment from the line, and deletes the attachment row.
    /// Authorizes: actor may edit the report + editable status + line belongs to report.
    /// Idempotent — no-op if the line has no attachment.
    /// </summary>
    Task<ExpenseMutationResult> RemoveAttachmentFromLineAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin,
        Guid lineId, CancellationToken ct = default);

    /// <summary>Submits the report. The payee snapshot is taken from the profile of the report's
    /// submitter, never the actor's. A report filed against a pre-approval claims the grant here,
    /// inside one transaction with the status change, and is restamped with the grant's account and
    /// cap as they are now; a first submit past the program's claim deadline is refused, a
    /// resubmit of the report already holding the grant is not.</summary>
    Task<ExpenseMutationResult> SubmitWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin, CancellationToken ct = default);

    /// <summary>Withdrawal stays the submitter's own decision — there is no on-behalf path. Withdrawn
    /// before approval, a pre-approved report releases its grant; after approval the grant stays
    /// consumed.</summary>
    Task<ExpenseMutationResult> WithdrawWithResultAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default);

    /// <summary>Sets the profile IBAN of whoever <paramref name="reportId"/> belongs to, and — while
    /// that report sits between submit and approval — rewrites its payee IBAN snapshot too, so the
    /// correction reaches the payment that has not gone out yet. Removing the IBAN is refused on
    /// those statuses. When <paramref name="actorUserId"/> is someone else, the audit entries name
    /// both and carry the raw IBAN (memory/code/audit-pii-subject-allowed.md).</summary>
    Task<ExpenseIbanSaveResult> SaveSubmitterIbanWithResultAsync(
        Guid reportId, Guid actorUserId, string? iban, CancellationToken ct = default);

    /// <summary>A non-null <paramref name="maxAmount"/> caps what this report pays out; null leaves it uncapped.</summary>
    Task<ExpenseMutationResult> CoordinatorEndorseWithResultAsync(
        Guid reportId, Guid coordinatorUserId, bool actorIsFinanceAdmin, decimal? maxAmount,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> CoordinatorRejectWithResultAsync(
        Guid reportId, Guid coordinatorUserId, bool actorIsFinanceAdmin, string reason,
        CancellationToken ct = default);

    /// <summary>
    /// Declines one line, with a reason: it stays on the report but leaves <c>Total</c> and
    /// <c>Payable</c> and is never pushed to Holded. The cap is untouched. Open to whoever holds
    /// Endorse or Approve on the report while it awaits their decision; proof rows are not declinable.
    /// </summary>
    Task<ExpenseMutationResult> DeclineLineWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin, Guid lineId, string reason,
        CancellationToken ct = default);

    /// <summary>Undoes a decline under the same conditions that allow one.</summary>
    Task<ExpenseMutationResult> UndoDeclineLineWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin, Guid lineId,
        CancellationToken ct = default);

    /// <summary>A non-null <paramref name="maxAmount"/> overrides any cap the coordinator set; a non-null
    /// <paramref name="overrideAccountNumber"/> rebooks the report to that account. On a pre-approved
    /// report the account is locked, a blank cap keeps the grant's current amount, and a cap above it
    /// is refused; approval consumes the grant in the same transaction.</summary>
    Task<ExpenseMutationResult> ApproveWithResultAsync(
        Guid reportId, Guid actorUserId, int? overrideAccountNumber, decimal? maxAmount,
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

    /// <summary>
    /// Reports with a written-off Holded push — the /Expenses/Review banner and flags. Empty unless
    /// <paramref name="actorIsFinanceAdmin"/>: only finance admins can act on a written-off push,
    /// and the list is queue-wide rather than scoped to the actor.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetFailedHoldedPushReportIdsAsync(bool actorIsFinanceAdmin, CancellationToken ct = default);
}

internal sealed record ExpenseMutationResult(bool Succeeded, ExpenseError? Error)
{
    public static ExpenseMutationResult Success { get; } = new(true, null);

    public static ExpenseMutationResult Failure(string resourceKey, params object[] arguments) =>
        new(false, new ExpenseError(resourceKey, arguments));

    public static ExpenseMutationResult OperatorFailure(string message) => new(false, new ExpenseError(null, [], message));
}

/// <summary>Line-add outcome; <see cref="LineId"/> is set on success so the caller can redirect
/// into the new line's flow (an invoice line continues to its proofs page).</summary>
internal sealed record ExpenseAddLineResult(bool Succeeded, ExpenseError? Error, Guid? LineId);

/// <summary>Draft-creation outcome; <see cref="ReportId"/> is set on success so the caller can
/// redirect into the new report's edit page.</summary>
internal sealed record ExpenseCreateDraftResult(bool Succeeded, ExpenseError? Error, Guid? ReportId);

/// <summary>A member resource key with format arguments, or feedback for an operator-only action.</summary>
internal sealed record ExpenseError(string? ResourceKey, object[] Arguments, string? OperatorMessage = null);

/// <summary>An uploaded file passed through to the service untouched.</summary>
internal sealed record ExpenseFileUpload(string FileName, string ContentType, Stream Content);

/// <summary>IBAN mutation outcome; the controller resolves MessageKey in ExpensesResource.</summary>
internal sealed record ExpenseIbanSaveResult(
    bool Succeeded,
    bool IsValidationError,
    string MessageKey);
