using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Budget.Contracts;
using Humans.Email.Contracts;
using Humans.Finance.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Holded.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Teams.Contracts;
using Humans.Expenses.Services.Dtos;
using Humans.Base.Helpers;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
using Humans.Expenses.Domain;
using Humans.Users.Contracts;

namespace Humans.Expenses.Services;

internal sealed class ExpenseReportService(
    IExpenseRepository repo,
    IFileStorage fileStorage,
    IBudgetService budgetService,
    ITeamService teamService,
    IUserService userService,
    IEmailService emailService,
    ExpensesEmails emails,
    IAuditLogService auditLogService,
    IAuditViewerService auditViewer,
    IHoldedService holded,
    IFinanceService finance,
    IFinanceWrite financeWrite,
    IPreapprovalService preapprovals,
    IPreapprovalWrite preapprovalWrites,
    IClock clock,
    ILogger<ExpenseReportService> logger,
    IOptions<TravelReimbursementConfig> travelConfig) : IExpenseReportManagement,
        IUserDataContributor
{
    private static readonly ExpenseReportStatus[] DraftEditStatuses = [ExpenseReportStatus.Draft];
    private static readonly ExpenseReportStatus[] FinanceCorrectionStatuses =
        [ExpenseReportStatus.Draft, ExpenseReportStatus.Submitted, ExpenseReportStatus.CoordinatorEndorsed];

    internal const string ExpenseReports = ExpenseReportExport.ExpenseReports;
    internal const string ExpenseAuditLog = ExpenseReportExport.ExpenseAuditLog;

    private ExpenseReportLines Lines => new(repo, reportAccess, attachments, reportAudit, logger, _travel);
    private ExpenseReportTransitions Transitions => new(
        repo, userService, emailService, emails, auditLogService, finance, preapprovals, preapprovalWrites, clock, logger, reportAccess, reportAudit);
    private ExpenseReportReview Review => new(repo, auditLogService, clock, reportAccess);
    private readonly ExpenseReportAudit reportAudit = new(userService, auditLogService, logger);
    private ExpensePayeeIban PayeeIban => new(repo, userService, financeWrite, auditLogService, clock, logger, reportAudit);
    private readonly ExpenseReportAccess reportAccess = new(repo, userService, budgetService, teamService, preapprovals, finance);
    private readonly ExpenseHoldedTimelineReader holdedTimeline = new(repo, finance, userService, holded);
    private readonly ExpenseAttachments attachments = new(repo, fileStorage, auditLogService, clock, logger);
    private readonly ExpenseReportExport reportExport = new(repo, userService, auditViewer);

    private readonly TravelReimbursementConfig _travel = travelConfig.Value;

    public Task<ExpenseReportDto?> GetAsync(Guid id, CancellationToken ct = default)
        => repo.GetByIdAsync(id, ct);

    public Task<IReadOnlyList<ExpenseReportDto>> GetAllAsync(CancellationToken ct = default)
        => repo.GetAllAsync(ct);

    /// <summary>
    /// The creditor balance already sums all of a member's outstanding docs, so whatever exceeds their own
    /// registered-unpaid report totals shows as fronted amounts and adjustments.
    /// </summary>
    public Task<ExpenseHoldedTimeline?> GetHoldedTimelineAsync(ExpenseReportDto report, CancellationToken ct = default) =>
        holdedTimeline.GetHoldedTimelineAsync(report, ct);

    public async Task<IReadOnlyList<ExpenseReportDto>> GetForSubmitterAsync(
        Guid submitterUserId, CancellationToken ct = default)
    {
        var submitter = await userService.GetUserInfoAsync(submitterUserId, ct);
        var reports = new List<ExpenseReportDto>();
        foreach (var id in submitter?.AllUserIds ?? [submitterUserId])
            reports.AddRange(await repo.GetForSubmitterAsync(id, ct));
        return reports;
    }

    public Task<IReadOnlyList<ExpenseReportDto>> GetReviewQueueAsync(CancellationToken ct = default) =>
        repo.GetForReviewQueueAsync(ct);

    public Task<ExpenseReportDto?> GetReportOwningAttachmentAsync(
        Guid attachmentId, CancellationToken ct = default) =>
        attachments.GetReportOwningAttachmentAsync(attachmentId, ct);

    public Task<ExpenseAttachmentDownload?> TryReadAttachmentAsync(
        ExpenseReportDto owningReport, Guid attachmentId, CancellationToken ct = default) =>
        attachments.TryReadAttachmentAsync(owningReport, attachmentId, ct);

    public Task<IReadOnlyList<ExpenseReportDto>> GetCoordinatorQueueAsync(
        Guid coordinatorUserId, CancellationToken ct = default) =>
        reportAccess.GetCoordinatorQueueAsync(coordinatorUserId, ct);

    public async Task<ExpenseCreateDraftResult> CreateDraftAsync(
        Guid submitterUserId, Guid actorUserId, int holdedAccountNumber, string? note,
        Guid? preapprovalGrantId = null,
        CancellationToken ct = default)
    {
        Guid? reportId = null;
        Guid? attemptedId = null;
        var result = await RunMutationAsync(ct, async () =>
        {
            var (resolved, refusal) = preapprovalGrantId is { } grantId
                ? await reportAccess.ResolveGrantBookingAsync(grantId, submitterUserId, heldGrantId: null, ct)
                : await reportAccess.ResolveBookingAsync(holdedAccountNumber, pinnedYearId: null, ct);
            if (refusal is not null) return refusal;
            var booking = resolved!;

            var now = clock.GetCurrentInstant();
            var report = new ExpenseReport
            {
                Id = Guid.NewGuid(),
                SubmitterUserId = submitterUserId,
                HoldedAccountNumber = booking.HoldedAccountNumber,
                HoldedAccountId = booking.HoldedAccountId,
                BudgetCategoryId = booking.BudgetCategoryId,
                BudgetYearId = booking.BudgetYearId,
                PreapprovalGrantId = preapprovalGrantId,
                Status = ExpenseReportStatus.Draft,
                Note = note,
                PayeeName = "",
                PayeeIban = "",
                Total = 0m,
                CreatedAt = now,
                UpdatedAt = now
            };
            attemptedId = report.Id;
            await repo.AddDraftAsync(report, ct);
            reportId = report.Id;

            // Self-created drafts stay unaudited — the report itself is the record. A report filed for
            // somebody else is an action taken on their behalf, so it leaves a trail naming both.
            if (actorUserId != submitterUserId)
            {
                await auditLogService.LogAsync(
                    ExpensesAuditActions.ExpenseCreatedOnBehalf,
                    AuditEntityTypes.Report, report.Id,
                    $"Created expense report on behalf of {await reportAudit.DescribeMemberAsync(submitterUserId, CancellationToken.None)}."
                    + ExpenseReportAudit.DescribeGrant(preapprovalGrantId),
                    actorUserId,
                    relatedEntityId: submitterUserId,
                    relatedEntityType: AuditSubjectTypes.User);
            }

            return ExpenseMutationResult.Success;
        }, "Error creating draft expense report for {SubmitterUserId}", submitterUserId);

        var uncertain = false;
        if (!result.Succeeded && reportId is null && attemptedId is { } candidate)
        {
            try
            {
                // A save can commit before its acknowledgement fails. Recover the draft instead of offering a duplicate.
                if (await repo.GetByIdAsync(candidate, CancellationToken.None) is not null)
                    reportId = candidate;
            }
            catch (Exception ex)
            {
                uncertain = true;
                logger.LogError(ex, "Could not verify new expense draft {ReportId} after a failed save", candidate);
            }
        }
        return new ExpenseCreateDraftResult(result.Succeeded, result.Error, reportId) { OutcomeUncertain = uncertain };
    }

    public Task<ExpenseMutationResult> UpdateDraftWithResultAsync(
        Guid reportId, Guid actorUserId,
        int holdedAccountNumber, string? note,
        Guid? preapprovalGrantId = null,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, () => Transitions.UpdateDraftWithResultAsync(reportId, actorUserId, DraftEditStatuses, holdedAccountNumber, note, preapprovalGrantId, ct),
            "Error updating expense report {ReportId}", reportId);

    public Task<ExpenseMutationResult> UpdateHeaderForFinanceCorrectionWithResultAsync(
        Guid reportId, Guid actorUserId,
        int holdedAccountNumber, string? note,
        Guid? preapprovalGrantId = null,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, () => Transitions.UpdateDraftWithResultAsync(reportId, actorUserId, FinanceCorrectionStatuses, holdedAccountNumber, note, preapprovalGrantId, ct),
            "Error updating expense report {ReportId}", reportId);

    public Task<ExpenseAddLineResult> AddLineWithResultAsync(
        Guid reportId, Guid actorUserId,
        string description, decimal amount, ExpenseLineType lineType = ExpenseLineType.Receipt,
        Guid? parentLineId = null, ExpenseFileUpload? file = null, CancellationToken ct = default) =>
        Lines.AddLineWithResultAsync(reportId, actorUserId, DraftEditStatuses,
            description, amount, lineType, parentLineId, file, ct);

    public Task<ExpenseAddLineResult> AddLineForFinanceCorrectionWithResultAsync(
        Guid reportId, Guid actorUserId,
        string description, decimal amount, ExpenseLineType lineType = ExpenseLineType.Receipt,
        Guid? parentLineId = null, ExpenseFileUpload? file = null, CancellationToken ct = default) =>
        Lines.AddLineWithResultAsync(reportId, actorUserId, FinanceCorrectionStatuses,
            description, amount, lineType, parentLineId, file, ct);

    public Task<ExpenseMutationResult> AddMileageLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        string origin, string destination, decimal km,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, () => Lines.AddMileageLineWithResultAsync(reportId, submitterUserId, origin, destination, km, ct),
            "Error adding mileage line to report {ReportId}", reportId);

    public Task<ExpenseMutationResult> AddPerDiemLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        PerDiemKind kind, int days, string? note,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, () => Lines.AddPerDiemLineWithResultAsync(reportId, submitterUserId, kind, days, note, ct),
            "Error adding per-diem line to report {ReportId}", reportId);

    public Task<ExpenseMutationResult> UpdateLineWithResultAsync(
        Guid reportId, Guid actorUserId,
        Guid lineId, string description, decimal amount,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, () => Lines.UpdateLineWithResultAsync(reportId, actorUserId, DraftEditStatuses, lineId, description, amount, ct),
            "Error updating line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> UpdateLineForFinanceCorrectionWithResultAsync(
        Guid reportId, Guid actorUserId,
        Guid lineId, string description, decimal amount,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, () => Lines.UpdateLineWithResultAsync(reportId, actorUserId, FinanceCorrectionStatuses, lineId, description, amount, ct),
            "Error updating line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> RemoveLineWithResultAsync(
        Guid reportId, Guid actorUserId, Guid lineId,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, () => Lines.RemoveLineWithResultAsync(reportId, actorUserId, DraftEditStatuses, lineId, ct),
            "Error removing line {LineId} from report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> RemoveLineForFinanceCorrectionWithResultAsync(
        Guid reportId, Guid actorUserId, Guid lineId,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, () => Lines.RemoveLineWithResultAsync(reportId, actorUserId, FinanceCorrectionStatuses, lineId, ct),
            "Error removing line {LineId} from report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> AttachFileToLineWithResultAsync(
        Guid reportId, Guid actorUserId,
        Guid lineId, string originalFileName, string contentType,
        Stream content, CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var refusal = await attachments.StoreAttachmentAsync(
                reportAccess, reportId, actorUserId, DraftEditStatuses, lineId, originalFileName, contentType, content, ct);
            return refusal is null ? ExpenseMutationResult.Success : new(false, refusal);
        }, "Error uploading attachment to line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> AttachFileToLineForFinanceCorrectionWithResultAsync(
        Guid reportId, Guid actorUserId,
        Guid lineId, string originalFileName, string contentType,
        Stream content, CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var refusal = await attachments.StoreAttachmentAsync(
                reportAccess, reportId, actorUserId, FinanceCorrectionStatuses, lineId, originalFileName, contentType, content, ct);
            return refusal is null ? ExpenseMutationResult.Success : new(false, refusal);
        }, "Error uploading attachment to line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> RemoveAttachmentFromLineAsync(
        Guid reportId, Guid actorUserId,
        Guid lineId, CancellationToken ct = default) =>
        RunMutationAsync(ct,
            () => attachments.RemoveAsync(reportAccess, reportId, actorUserId, DraftEditStatuses, lineId, ct),
            "Error removing attachment from line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> RemoveAttachmentFromLineForFinanceCorrectionAsync(
        Guid reportId, Guid actorUserId,
        Guid lineId, CancellationToken ct = default) =>
        RunMutationAsync(ct,
            () => attachments.RemoveAsync(reportAccess, reportId, actorUserId, FinanceCorrectionStatuses, lineId, ct),
            "Error removing attachment from line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> SubmitWithResultAsync(
        Guid reportId, Guid actorUserId, CancellationToken ct = default) =>
        RunMutationAsync(ct, () => Transitions.SubmitWithResultAsync(reportId, actorUserId, ct),
            "Error submitting expense report {ReportId}", reportId);

    /// <summary>
    /// A member-facing refusal, or null when the claim should be attempted. Mirrors
    /// <see cref="IPreapprovalWrite.ClaimAsync"/>: the report already holding the grant resubmits past the deadline.
    /// </summary>
    public Task<ExpenseMutationResult> WithdrawWithResultAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default) =>
        RunMutationAsync(ct, () => Transitions.WithdrawWithResultAsync(reportId, submitterUserId, ct),
            "Error withdrawing expense report {ReportId}", reportId);

    public Task<ExpenseIbanSaveResult> SaveSubmitterIbanWithResultAsync(
        Guid reportId, Guid actorUserId, string? iban, CancellationToken ct = default) =>
        PayeeIban.SaveSubmitterIbanWithResultAsync(reportId, actorUserId, iban, ct);

    private async Task<ExpenseMutationResult> RunMutationAsync(
        CancellationToken ct,
        Func<Task<ExpenseMutationResult>> mutation,
        string logMessage,
        params object?[] logArgs)
    {
        try
        {
            var result = await mutation();
            if (!result.Succeeded && result.Error is { } refusal)
                logger.LogWarning($"{logMessage}: {{Reason}}", [.. logArgs, refusal.ResourceKey ?? refusal.OperatorMessage]);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, logMessage, logArgs);
            // Unexpected diagnostics belong in the log; controllers supply the
            // localized fallback for a failed mutation with no validation detail.
            return new ExpenseMutationResult(Succeeded: false, Error: null);
        }
    }

    public Task<ExpenseMutationResult> CoordinatorEndorseWithResultAsync(
        Guid reportId, Guid coordinatorUserId, decimal? maxAmount,
        CancellationToken ct = default, string? auditReviewerLabel = null) =>
        RunMutationAsync(ct, () => Review.CoordinatorEndorseWithResultAsync(reportId, coordinatorUserId, maxAmount, ct, auditReviewerLabel),
            "Error endorsing expense report {ReportId}", reportId);

    public Task<ExpenseMutationResult> CoordinatorRejectWithResultAsync(
        Guid reportId, Guid coordinatorUserId, string reason,
        CancellationToken ct = default, string? auditReviewerLabel = null) =>
        RunMutationAsync(ct, () => Review.CoordinatorRejectWithResultAsync(reportId, coordinatorUserId, reason, ct, auditReviewerLabel),
            "Error coordinator-rejecting expense report {ReportId}", reportId);

    public Task<ExpenseMutationResult> DeclineLineWithResultAsync(
        Guid reportId, Guid actorUserId, Guid lineId, string reason,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, () => Review.DeclineLineWithResultAsync(reportId, actorUserId, lineId, reason, ct),
            "Error declining line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> DeclineLineForFinanceReviewWithResultAsync(
        Guid reportId, Guid actorUserId, Guid lineId, string reason,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, () => Review.DeclineLineForFinanceReviewWithResultAsync(reportId, actorUserId, lineId, reason, ct),
            "Error declining line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> UndoDeclineLineWithResultAsync(
        Guid reportId, Guid actorUserId, Guid lineId,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, () => Review.UndoDeclineLineWithResultAsync(reportId, actorUserId, lineId, ct),
            "Error undoing decline of line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> UndoDeclineLineForFinanceReviewWithResultAsync(
        Guid reportId, Guid actorUserId, Guid lineId,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, () => Review.UndoDeclineLineForFinanceReviewWithResultAsync(reportId, actorUserId, lineId, ct),
            "Error undoing decline of line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> ApproveWithResultAsync(
        Guid reportId, Guid actorUserId, int? overrideAccountNumber, decimal? maxAmount,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, () => Transitions.ApproveWithResultAsync(reportId, actorUserId, overrideAccountNumber, maxAmount, ct),
            "Error approving expense report {ReportId}", reportId);

    public Task<ExpenseMutationResult> FinanceRejectWithResultAsync(
        Guid reportId, Guid actorUserId, string reason,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, () => Review.FinanceRejectWithResultAsync(reportId, actorUserId, reason, ct),
            "Error finance-rejecting expense report {ReportId}", reportId);

    public Task<IReadOnlyList<Guid>> GetFailedHoldedPushReportIdsAsync(CancellationToken ct = default)
        => repo.GetFailedOutboxReportIdsAsync(ct);

    public Task<ExpenseMutationResult> RequeueHoldedPushWithResultAsync(
        Guid reportId, Guid actorUserId, CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var requeued = await repo.RequeueOutboxForReportAsync(reportId, ct);
            if (!requeued) return ExpenseMutationResult.OperatorFailure(
                        "This report has no failed or retrying Holded push to re-queue.");

            await auditLogService.LogAsync(
                ExpensesAuditActions.ExpenseHoldedRequeued,
                AuditEntityTypes.Report, reportId,
                "Finance re-queued the Holded push.",
                actorUserId);

            return ExpenseMutationResult.Success;

        }, "Error re-queuing Holded push for expense report {ReportId}", reportId);

    /// <summary>Includes accounts merged into this one. The member's IBAN is Finance's slice.</summary>
    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        reportExport.ContributeForUserAsync(userId, ct);

    private const string FiscalRetention =
        "Retained in full, nothing erased: the voucher keeps the payee's legal name, their " +
        "bank account (IBAN, stored unmasked — the export masks it, the row does not), the " +
        "amounts and dates, the free-text note and per-line descriptions, the approval trail " +
        "and any uploaded receipt. A reimbursement is an accounting voucher and Spanish law " +
        "requires the books and their supporting documents be kept 6 years (Código de " +
        "Comercio Art. 30) and 4 years for tax purposes (Ley 58/2003 Art. 66) — an " +
        "incomplete voucher is not a voucher. GDPR Art. 17(3)(b).";

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ExpenseReports] = FiscalRetention,
            [ExpenseAuditLog] = FiscalRetention
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
}
