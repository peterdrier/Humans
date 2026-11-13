using Humans.Expenses.Contracts;
using Humans.Expenses.Domain;
using Humans.Base.Interfaces;

namespace Humans.Expenses.Services;

/// <summary>
/// Callers authorize report mutations. Ordinary edits require Draft; explicit Finance-correction commands also
/// accept Submitted and CoordinatorEndorsed. The actor identifies audit attribution, never permission.
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

    /// <summary>Updates a Draft after caller authorization; a pre-approval may be kept, swapped or dropped.</summary>
    Task<ExpenseMutationResult> UpdateDraftWithResultAsync(
        Guid reportId, Guid actorUserId,
        int holdedAccountNumber, string? note,
        Guid? preapprovalGrantId = null,
        CancellationToken ct = default);

    /// <summary>Updates a Draft, Submitted or CoordinatorEndorsed report after caller authorization.
    /// Past Draft, a pre-approval and its account remain locked; other accounts stay in their booked year.</summary>
    Task<ExpenseMutationResult> UpdateHeaderForFinanceCorrectionWithResultAsync(
        Guid reportId, Guid actorUserId,
        int holdedAccountNumber, string? note,
        Guid? preapprovalGrantId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Receipt or Invoice only; travel amounts are computed. <paramref name="file"/> is validated before the line is
    /// created, so a bad upload leaves nothing half-made. A non-null <paramref name="parentLineId"/> adds a proof row
    /// backing that Invoice line.
    /// </summary>
    Task<ExpenseAddLineResult> AddLineWithResultAsync(
        Guid reportId, Guid actorUserId,
        string description, decimal amount,
        ExpenseLineType lineType = ExpenseLineType.Receipt,
        Guid? parentLineId = null,
        ExpenseFileUpload? file = null,
        CancellationToken ct = default);

    /// <summary>Corrects a Draft, Submitted or CoordinatorEndorsed report after caller authorization.</summary>
    Task<ExpenseAddLineResult> AddLineForFinanceCorrectionWithResultAsync(
        Guid reportId, Guid actorUserId,
        string description, decimal amount,
        ExpenseLineType lineType = ExpenseLineType.Receipt,
        Guid? parentLineId = null,
        ExpenseFileUpload? file = null,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> UpdateLineWithResultAsync(
        Guid reportId, Guid actorUserId,
        Guid lineId, string description, decimal amount,
        CancellationToken ct = default);

    /// <summary>Corrects a Draft, Submitted or CoordinatorEndorsed report after caller authorization.</summary>
    Task<ExpenseMutationResult> UpdateLineForFinanceCorrectionWithResultAsync(
        Guid reportId, Guid actorUserId,
        Guid lineId, string description, decimal amount,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> RemoveLineWithResultAsync(
        Guid reportId, Guid actorUserId, Guid lineId,
        CancellationToken ct = default);

    /// <summary>Corrects a Draft, Submitted or CoordinatorEndorsed report after caller authorization.</summary>
    Task<ExpenseMutationResult> RemoveLineForFinanceCorrectionWithResultAsync(
        Guid reportId, Guid actorUserId, Guid lineId,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> AttachFileToLineWithResultAsync(
        Guid reportId, Guid actorUserId,
        Guid lineId, string originalFileName, string contentType,
        Stream content, CancellationToken ct = default);

    /// <summary>Corrects a Draft, Submitted or CoordinatorEndorsed report after caller authorization.</summary>
    Task<ExpenseMutationResult> AttachFileToLineForFinanceCorrectionWithResultAsync(
        Guid reportId, Guid actorUserId,
        Guid lineId, string originalFileName, string contentType,
        Stream content, CancellationToken ct = default);

    /// <summary>Idempotent: no-op if the line has no attachment.</summary>
    Task<ExpenseMutationResult> RemoveAttachmentFromLineAsync(
        Guid reportId, Guid actorUserId,
        Guid lineId, CancellationToken ct = default);

    /// <summary>Corrects a Draft, Submitted or CoordinatorEndorsed report after caller authorization.</summary>
    Task<ExpenseMutationResult> RemoveAttachmentFromLineForFinanceCorrectionAsync(
        Guid reportId, Guid actorUserId,
        Guid lineId, CancellationToken ct = default);

    /// <summary>The caller authorizes submission; the service requires a Draft. The payee snapshot comes from the
    /// submitter's profile, never the actor's. A pre-approved report claims
    /// its grant in the same transaction and is restamped with the grant's account and cap; a first submit past the
    /// claim deadline is refused, a resubmit of the report holding the grant is not.</summary>
    Task<ExpenseMutationResult> SubmitWithResultAsync(
        Guid reportId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>The submitter's own decision; there is no on-behalf path. A pre-approved report releases its grant.
    /// Approved reports are refused: approval has already queued the Holded push.</summary>
    Task<ExpenseMutationResult> WithdrawWithResultAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default);

    /// <summary>Sets the submitter's profile IBAN and, while the report is pending approval, its payee snapshot too.
    /// Removal is refused on those statuses. An actor other than the submitter gets audit entries naming both, with
    /// the raw IBAN.</summary>
    Task<ExpenseIbanSaveResult> SaveSubmitterIbanWithResultAsync(
        Guid reportId, Guid actorUserId, string? iban, CancellationToken ct = default);

    /// <summary>A null cap uses the grant ceiling on pre-approved reports and leaves other reports uncapped. The caller authorizes the reviewer.</summary>
    /// <remarks><paramref name="auditReviewerLabel"/> records review context in audit text only.</remarks>
    Task<ExpenseMutationResult> CoordinatorEndorseWithResultAsync(
        Guid reportId, Guid coordinatorUserId, decimal? maxAmount,
        CancellationToken ct = default, string? auditReviewerLabel = null);

    /// <summary>The caller authorizes the reviewer; the repository enforces Submitted status.</summary>
    /// <remarks><paramref name="auditReviewerLabel"/> records review context in audit text only.</remarks>
    Task<ExpenseMutationResult> CoordinatorRejectWithResultAsync(
        Guid reportId, Guid coordinatorUserId, string reason,
        CancellationToken ct = default, string? auditReviewerLabel = null);

    /// <summary>
    /// First-review command for Submitted reports. The caller authorizes the reviewer. Declined lines leave
    /// Total and Payable but stay on the report; proof rows cannot be declined and the cap is untouched.
    /// </summary>
    Task<ExpenseMutationResult> DeclineLineWithResultAsync(
        Guid reportId, Guid actorUserId, Guid lineId, string reason,
        CancellationToken ct = default);

    /// <summary>The Finance-review command accepts Submitted or CoordinatorEndorsed reports after caller authorization.</summary>
    Task<ExpenseMutationResult> DeclineLineForFinanceReviewWithResultAsync(
        Guid reportId, Guid actorUserId, Guid lineId, string reason,
        CancellationToken ct = default);

    /// <summary>Undoes a first-review decline on a Submitted report after caller authorization.</summary>
    Task<ExpenseMutationResult> UndoDeclineLineWithResultAsync(
        Guid reportId, Guid actorUserId, Guid lineId,
        CancellationToken ct = default);

    /// <summary>The Finance-review command accepts Submitted or CoordinatorEndorsed reports after caller authorization.</summary>
    Task<ExpenseMutationResult> UndoDeclineLineForFinanceReviewWithResultAsync(
        Guid reportId, Guid actorUserId, Guid lineId,
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
    /// Queue-wide, not scoped to the actor. The caller must authorize Finance access before loading these IDs.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetFailedHoldedPushReportIdsAsync(CancellationToken ct = default);
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

/// <summary><see cref="ReportId"/> identifies a confirmed saved draft, including failures after persistence.</summary>
internal sealed record ExpenseCreateDraftResult(bool Succeeded, ExpenseError? Error, Guid? ReportId)
{
    public bool OutcomeUncertain { get; init; }
}

/// <summary>A member resource key with format arguments, or feedback for an operator-only action.</summary>
internal sealed record ExpenseError(string? ResourceKey, object[] Arguments, string? OperatorMessage = null);

internal sealed record ExpenseFileUpload(string FileName, string ContentType, Stream Content);

/// <summary>The controller resolves MessageKey in ExpensesResource.</summary>
internal sealed record ExpenseIbanSaveResult(
    bool Succeeded,
    bool IsValidationError,
    string MessageKey);
