using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
using Humans.Expenses.Domain;
using Humans.Base.Interfaces;

namespace Humans.Expenses.Services;

/// <summary>
/// Callers authorize report mutations. Ordinary edits require Draft; explicit Finance-correction commands also
/// accept Submitted and CoordinatorEndorsed. The actor identifies audit attribution, never permission.
/// </summary>
internal interface IExpenseReportManagement : IExpenseReportService
{
    Task<IReadOnlyList<ExpenseReportDto>> GetForSubmitterAsync(
        Guid submitterUserId, CancellationToken ct = default);

    /// <summary>All statuses.</summary>
    Task<IReadOnlyList<ExpenseReportDto>> GetAllAsync(CancellationToken ct = default);

    /// <summary>The budget category is derived from the account. A non-null <paramref name="preapprovalGrantId"/> must
    /// be claimable by the submitter, and the account then comes from the grant, with no category. An actor other than
    /// the submitter is audited as on-behalf.</summary>
    Task<ExpenseCreateDraftResult> CreateDraftAsync(
        Guid submitterUserId, Guid actorUserId, int holdedAccountNumber, string? note,
        Guid? preapprovalGrantId = null,
        CancellationToken ct = default);

    /// <summary>Updates a Draft after caller authorization; a pre-approval may be kept, swapped or dropped.
    /// Like every header, line and attachment edit here, an Admin or Finance Admin actor may also edit a Submitted or
    /// CoordinatorEndorsed report. Past Draft, a pre-approval and its account remain locked; other accounts stay in
    /// their booked year.</summary>
    Task<ExpenseMutationResult> UpdateHeaderAsync(
        Guid reportId, Guid actorUserId,
        int holdedAccountNumber, string? note,
        Guid? preapprovalGrantId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Receipt or Invoice only; travel amounts are computed. <paramref name="file"/> is validated before the line is
    /// created, so a bad upload leaves nothing half-made. A non-null <paramref name="parentLineId"/> adds a proof row
    /// backing that Invoice line.
    /// </summary>
    Task<ExpenseAddLineResult> AddLineAsync(
        Guid reportId, Guid actorUserId,
        string description, decimal amount,
        ExpenseLineType lineType = ExpenseLineType.Receipt,
        Guid? parentLineId = null,
        ExpenseFileUpload? file = null,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> UpdateLineAsync(
        Guid reportId, Guid actorUserId,
        Guid lineId, string description, decimal amount,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> RemoveLineAsync(
        Guid reportId, Guid actorUserId, Guid lineId,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> AttachFileToLineAsync(
        Guid reportId, Guid actorUserId,
        Guid lineId, string originalFileName, string contentType,
        Stream content, CancellationToken ct = default);

    /// <summary>Idempotent: no-op if the line has no attachment.</summary>
    Task<ExpenseMutationResult> RemoveAttachmentFromLineAsync(
        Guid reportId, Guid actorUserId,
        Guid lineId, CancellationToken ct = default);

    /// <summary>The caller authorizes submission; the service requires a Draft. The payee snapshot comes from the
    /// submitter's profile, never the actor's. A pre-approved report claims
    /// its grant in the same transaction and is restamped with the grant's account and cap; a first submit past the
    /// claim deadline is refused, a resubmit of the report holding the grant is not.</summary>
    Task<ExpenseMutationResult> SubmitAsync(
        Guid reportId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>The submitter's own decision; there is no on-behalf path. A pre-approved report releases its grant.
    /// Approved reports are refused: approval has already queued the Holded push.</summary>
    Task<ExpenseMutationResult> WithdrawAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default);

    /// <summary>Sets the submitter's profile IBAN and, while the report is pending approval, its payee snapshot too.
    /// Removal is refused on those statuses. An actor other than the submitter gets audit entries naming both, with
    /// the raw IBAN.</summary>
    Task<ExpenseIbanSaveResult> SaveSubmitterIbanAsync(
        Guid reportId, Guid actorUserId, string? iban, CancellationToken ct = default);

    Task<ExpenseMutationResult> AddMileageLineAsync(
        Guid reportId, Guid submitterUserId,
        string origin, string destination, decimal km,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> AddPerDiemLineAsync(
        Guid reportId, Guid submitterUserId,
        PerDiemKind kind, int days, string? note,
        CancellationToken ct = default);

    /// <summary>
    /// What a report books to from the account picked: a budget-category account also carries its
    /// category and year. <paramref name="pinnedYearId"/> keeps a submitted report in its own year.
    /// </summary>
    Task<(ExpenseBooking? Booking, ExpenseMutationResult? Refusal)> ResolveBookingAsync(
        int holdedAccountNumber, Guid? pinnedYearId, CancellationToken ct);
}

/// <summary>Coordinator and finance decisions on submitted reports, and the Holded push re-queue.</summary>
internal interface IExpenseReview : IApplicationService
{
    Task<IReadOnlyList<ExpenseReportDto>> GetCoordinatorQueueAsync(
        Guid coordinatorUserId, CancellationToken ct = default);

    /// <summary>A null cap uses the grant ceiling on pre-approved reports and leaves other reports uncapped. The caller authorizes the reviewer.</summary>
    /// <remarks><paramref name="auditReviewerLabel"/> records review context in audit text only.</remarks>
    Task<ExpenseMutationResult> CoordinatorEndorseAsync(
        Guid reportId, Guid coordinatorUserId, decimal? maxAmount,
        CancellationToken ct = default, string? auditReviewerLabel = null);

    /// <summary>The caller authorizes the reviewer; the repository enforces Submitted status.</summary>
    /// <remarks><paramref name="auditReviewerLabel"/> records review context in audit text only.</remarks>
    Task<ExpenseMutationResult> CoordinatorRejectAsync(
        Guid reportId, Guid coordinatorUserId, string reason,
        CancellationToken ct = default, string? auditReviewerLabel = null);

    /// <summary>
    /// For Submitted reports, and CoordinatorEndorsed ones when the actor is Admin or Finance Admin. The caller
    /// authorizes the reviewer. Declined lines leave
    /// Total and Payable but stay on the report; proof rows cannot be declined and the cap is untouched.
    /// </summary>
    Task<ExpenseMutationResult> DeclineLineAsync(
        Guid reportId, Guid actorUserId, Guid lineId, string reason,
        CancellationToken ct = default);

    /// <summary>Undoes a decline after caller authorization, in the same status window as declining.</summary>
    Task<ExpenseMutationResult> UndoDeclineLineAsync(
        Guid reportId, Guid actorUserId, Guid lineId,
        CancellationToken ct = default);

    /// <summary>A non-null <paramref name="maxAmount"/> overrides the coordinator's cap; a non-null
    /// <paramref name="overrideAccountNumber"/> rebooks. On a pre-approved report the account is locked, a blank cap
    /// keeps the grant's amount, and a higher cap is refused; approval consumes the grant in the same transaction.</summary>
    Task<ExpenseMutationResult> ApproveAsync(
        Guid reportId, Guid actorUserId, int? overrideAccountNumber, decimal? maxAmount,
        CancellationToken ct = default);

    Task<ExpenseMutationResult> FinanceRejectAsync(
        Guid reportId, Guid actorUserId, string reason,
        CancellationToken ct = default);

    /// <summary>
    /// Re-queues a written-off or backing-off push and resets the retry budget. Fails when the report has neither.
    /// </summary>
    Task<ExpenseMutationResult> RequeueHoldedPushAsync(
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
