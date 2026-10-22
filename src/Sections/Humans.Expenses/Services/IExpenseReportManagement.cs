using Humans.Expenses.Contracts;
using Humans.Expenses.Domain;
using Humans.Base.Interfaces;

namespace Humans.Expenses.Services;

/// <summary>
/// A report belongs to its <c>SubmitterUserId</c>; the actor is whoever is changing it. Setting
/// <c>actorIsFinanceAdmin</c> waives the ownership match and widens the editable window to Draft, Submitted and
/// CoordinatorEndorsed. These checks are defence in depth behind the resource-based handler.
/// </summary>
internal interface IExpenseReportManagement : IExpenseReportService, IApplicationService
{
    Task<IReadOnlyList<ExpenseReportDto>> GetForSubmitterAsync(
        Guid submitterUserId, CancellationToken ct = default);

    Task<IReadOnlyList<ExpenseReportDto>> GetCoordinatorQueueAsync(
        Guid coordinatorUserId, CancellationToken ct = default);

    /// <summary>All statuses.</summary>
    Task<IReadOnlyList<ExpenseReportDto>> GetAllAsync(CancellationToken ct = default);

    /// <summary>The budget category is derived from the account. A non-null <paramref name="preapprovalGrantId"/> must
    /// be claimable by the submitter, and the account then comes from the grant, with no category. An actor other than
    /// the submitter is audited as on-behalf.</summary>
    Task<ExpenseCreateDraftResult> CreateDraftAsync(
        Guid submitterUserId, Guid actorUserId, int holdedAccountNumber, string? note,
        Guid? preapprovalGrantId = null,
        CancellationToken ct = default);

    /// <summary>On a Draft, <paramref name="preapprovalGrantId"/> may keep, swap or drop the pre-approval; past Draft
    /// it and its account are locked and only the note changes.</summary>
    Task<ExpenseMutationResult> UpdateDraftWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin,
        int holdedAccountNumber, string? note,
        Guid? preapprovalGrantId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Receipt or Invoice only; travel amounts are computed. <paramref name="file"/> is validated before the line is
    /// created, so a bad upload leaves nothing half-made. A non-null <paramref name="parentLineId"/> adds a proof row
    /// backing that Invoice line.
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

    /// <summary>Idempotent: no-op if the line has no attachment.</summary>
    Task<ExpenseMutationResult> RemoveAttachmentFromLineAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin,
        Guid lineId, CancellationToken ct = default);

    /// <summary>The payee snapshot comes from the submitter's profile, never the actor's. A pre-approved report claims
    /// its grant in the same transaction and is restamped with the grant's account and cap; a first submit past the
    /// claim deadline is refused, a resubmit of the report holding the grant is not.</summary>
    Task<ExpenseMutationResult> SubmitWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin, CancellationToken ct = default);

    /// <summary>The submitter's own decision; there is no on-behalf path. A pre-approved report releases its grant.
    /// Approved reports are refused: approval has already queued the Holded push.</summary>
    Task<ExpenseMutationResult> WithdrawWithResultAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default);

    /// <summary>Sets the submitter's profile IBAN and, while the report is pending approval, its payee snapshot too.
    /// Removal is refused on those statuses. An actor other than the submitter gets audit entries naming both, with
    /// the raw IBAN.</summary>
    Task<ExpenseIbanSaveResult> SaveSubmitterIbanWithResultAsync(
        Guid reportId, Guid actorUserId, string? iban, CancellationToken ct = default);

    /// <summary>A null <paramref name="maxAmount"/> leaves the report uncapped.</summary>
    Task<ExpenseMutationResult> CoordinatorEndorseWithResultAsync(
        Guid reportId, Guid coordinatorUserId, bool actorIsFinanceAdmin, decimal? maxAmount,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> CoordinatorRejectWithResultAsync(
        Guid reportId, Guid coordinatorUserId, bool actorIsFinanceAdmin, string reason,
        CancellationToken ct = default);

    /// <summary>
    /// The line stays but leaves <c>Total</c> and <c>Payable</c> and is never pushed. The cap is untouched. Open to
    /// whoever holds Endorse or Approve while the report awaits them; proof rows are not declinable.
    /// </summary>
    Task<ExpenseMutationResult> DeclineLineWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin, Guid lineId, string reason,
        CancellationToken ct = default);

    /// <summary>Undoes a decline under the same conditions that allow one.</summary>
    Task<ExpenseMutationResult> UndoDeclineLineWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin, Guid lineId,
        CancellationToken ct = default);

    /// <summary>A non-null <paramref name="maxAmount"/> overrides the coordinator's cap; a non-null
    /// <paramref name="overrideAccountNumber"/> rebooks. On a pre-approved report the account is locked, a blank cap
    /// keeps the grant's amount, and a higher cap is refused; approval consumes the grant in the same transaction.</summary>
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
    /// Re-queues a written-off or backing-off push and resets the retry budget. Fails when the report has neither.
    /// </summary>
    Task<ExpenseMutationResult> RequeueHoldedPushWithResultAsync(
        Guid reportId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Queue-wide, not scoped to the actor. Empty unless <paramref name="actorIsFinanceAdmin"/>: only finance admins can
    /// act on a written-off push.
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

/// <summary><see cref="LineId"/> is set on success so the caller can redirect into the new line's flow.</summary>
internal sealed record ExpenseAddLineResult(bool Succeeded, ExpenseError? Error, Guid? LineId);

/// <summary><see cref="ReportId"/> is set on success.</summary>
internal sealed record ExpenseCreateDraftResult(bool Succeeded, ExpenseError? Error, Guid? ReportId);

/// <summary>A member resource key with format arguments, or feedback for an operator-only action.</summary>
internal sealed record ExpenseError(string? ResourceKey, object[] Arguments, string? OperatorMessage = null);

internal sealed record ExpenseFileUpload(string FileName, string ContentType, Stream Content);

/// <summary>The controller resolves MessageKey in ExpensesResource.</summary>
internal sealed record ExpenseIbanSaveResult(
    bool Succeeded,
    bool IsValidationError,
    string MessageKey);
