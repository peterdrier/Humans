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
using System.Globalization;
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
    internal const string ExpenseReports = ExpenseReportExport.ExpenseReports;
    internal const string ExpenseAuditLog = ExpenseReportExport.ExpenseAuditLog;

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

    public Task<IReadOnlyList<ExpenseReportDto>> GetReviewQueueAsync(
        Guid viewerUserId, bool isFinanceAdmin, CancellationToken ct = default) =>
        reportAccess.GetReviewQueueAsync(viewerUserId, isFinanceAdmin, ct);

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
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin,
        int holdedAccountNumber, string? note,
        Guid? preapprovalGrantId = null,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, () => Transitions.UpdateDraftWithResultAsync(reportId, actorUserId, actorIsFinanceAdmin, holdedAccountNumber, note, preapprovalGrantId, ct),
            "Error updating expense report {ReportId}", reportId);

    private async Task<(ExpenseAddLineResult Result, ExpenseReportDto? Report)> AddLineWithoutAuditAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin,
        string description, decimal amount, ExpenseLineType lineType,
        Guid? parentLineId, CancellationToken ct)
    {
        var (editableReport, refusal) = await GetEditableReportAsync(reportId, actorUserId, actorIsFinanceAdmin, ct);
        if (refusal is not null) return (new(false, refusal.Error, null), null);
        var report = editableReport!;

        if (parentLineId is { } parentId)
        {
            // A proof row is a Receipt backing an Invoice line on the same report. One level only.
            if (lineType != ExpenseLineType.Receipt)
                return (new(false, ExpenseMutationResult.Failure("Expenses_Validation_ProofRowsMustBeReceipts").Error, null), null);
            var parent = report.Lines.FirstOrDefault(l => l.Id == parentId);
            if (parent is null) return (new(false, ExpenseMutationResult.Failure("Expenses_Validation_ParentLineNotFound").Error, null), null);
            if (parent.LineType != ExpenseLineType.Invoice)
                return (new(false, ExpenseMutationResult.Failure("Expenses_Validation_ProofRowsRequireInvoice").Error, null), null);
        }

        var line = new ExpenseLine
        {
            Id = Guid.NewGuid(),
            ExpenseReportId = reportId,
            Description = description,
            Amount = amount,
            LineType = lineType,
            ParentLineId = parentLineId
        };
        try
        {
            var ok = await repo.AddLineAsync(reportId, line, ct);
            if (!ok) throw new InvalidOperationException("Failed to add line.");
        }
        catch
        {
            await TryRollbackAddedLineAsync(reportId, line.Id);
            throw;
        }

        return (new(true, null, line.Id), report);
    }

    public async Task<ExpenseAddLineResult> AddLineWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin,
        string description, decimal amount,
        ExpenseLineType lineType = ExpenseLineType.Receipt,
        Guid? parentLineId = null,
        ExpenseFileUpload? file = null,
        CancellationToken ct = default)
    {
        ExpenseAddLineResult Reject(ExpenseError refusal)
        {
            logger.LogWarning("Error adding line to report {ReportId}: {Reason}", reportId, refusal.ResourceKey);
            return new(false, refusal, null);
        }

        try
        {
            // This path takes free-text amounts; travel lines are computed.
            if (lineType is not (ExpenseLineType.Receipt or ExpenseLineType.Invoice))
                return Reject(new("Expenses_Validation_OnlyReceiptAndInvoiceLines", []));
            if (decimal.Round(amount, 2) != amount)
                return Reject(new("Expenses_Validation_AmountPrecision", []));
            // Validate the file before creating anything, so a bad upload leaves no half-made line.
            if (file is not null && ExpenseAttachments.ValidateAttachmentUpload(file.FileName, file.ContentType, file.Content).Refusal is { } uploadRefusal)
                return Reject(uploadRefusal);

            var (added, report) = await AddLineWithoutAuditAsync(
                reportId, actorUserId, actorIsFinanceAdmin,
                description, amount, lineType, parentLineId, ct);
            if (added.Error is { } addRefusal) return Reject(addRefusal);
            var lineId = added.LineId!.Value;
            ExpenseError? attachmentRefusal = null;
            try
            {
                if (file is not null)
                    attachmentRefusal = await attachments.StoreAttachmentAsync(
                        reportAccess, reportId, actorUserId, actorIsFinanceAdmin, lineId, file.FileName, file.ContentType, file.Content, ct);
                if (attachmentRefusal is null)
                    await reportAudit.AuditOnBehalfEditAsync(report!, actorUserId,
                        $"Added {(parentLineId is null ? "line" : "proof row")} \"{description}\" €{amount}", CancellationToken.None);
            }
            catch
            {
                // The form retries the whole add. Undo the new line and any linked upload before failing.
                await TryRollbackAddedLineAsync(reportId, lineId);
                throw;
            }
            if (attachmentRefusal is not null)
            {
                if (!await TryRollbackAddedLineAsync(reportId, lineId))
                    return new ExpenseAddLineResult(false, null, null);
                return Reject(attachmentRefusal);
            }
            return new ExpenseAddLineResult(true, null, lineId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error adding line to report {ReportId}", reportId);
            return new ExpenseAddLineResult(false, null, null);
        }
    }

    private async Task<bool> TryRollbackAddedLineAsync(Guid reportId, Guid lineId)
    {
        ExpenseReportDto? report = null;
        try
        {
            report = await repo.GetByIdAsync(reportId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not read attachments before rolling back new line {LineId}", lineId);
        }
        var candidates = report?.Lines.Where(line => line.Id == lineId || line.ParentLineId == lineId)
            .Select(line => line.Attachment).OfType<ExpenseAttachmentDto>()
            .Select(attachment => (attachment.Id, attachment.Extension)).ToList() ?? [];
        IReadOnlyList<ExpenseAttachment>? removed = null;
        try
        {
            try
            {
                removed = await repo.RemoveLineAsync(reportId, lineId, CancellationToken.None);
            }
            finally
            {
                // Retain the pre-delete ids too: compensation can itself commit before its acknowledgement fails.
                await attachments.CleanupUnlinkedAttachmentsAsync(
                    removed is null ? candidates : removed.Select(a => (a.Id, a.Extension)), lineId);
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not roll back new expense line {LineId} on report {ReportId}", lineId, reportId);
            return false;
        }
    }

    private async Task<ExpenseMutationResult> AddComputedLineAsync(
        Guid reportId, Guid actorUserId, string description, decimal amount, ExpenseLineType lineType,
        CancellationToken ct)
    {
        var (added, report) = await AddLineWithoutAuditAsync(
            reportId, actorUserId, false, description, amount, lineType, null, ct);
        if (added.Error is not null) return new(false, added.Error);
        await reportAudit.AuditOnBehalfEditAsync(report!, actorUserId, $"Added line \"{description}\" €{amount}", ct);
        return ExpenseMutationResult.Success;
    }

    public Task<ExpenseMutationResult> AddMileageLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        string origin, string destination, decimal km,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var rate = _travel.MileageRatePerKm;
            var amount = Math.Round(km * rate, 2, MidpointRounding.AwayFromZero);
            var description =
                $"{origin.Trim()} to {destination.Trim()}, " +
                $"{km.ToString("0.#", CultureInfo.InvariantCulture)} km @ " +
                $"€{rate.ToString("0.00", CultureInfo.InvariantCulture)} = " +
                $"€{amount.ToString("0.00", CultureInfo.InvariantCulture)}";
            return await AddComputedLineAsync(reportId, submitterUserId, description, amount, ExpenseLineType.Mileage, ct);
        }, "Error adding mileage line to report {ReportId}", reportId);

    public Task<ExpenseMutationResult> AddPerDiemLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        PerDiemKind kind, int days, string? note,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var rate = kind == PerDiemKind.Overnight ? _travel.PerDiemOvernightRate : _travel.PerDiemDayTripRate;
            var amount = Math.Round(days * rate, 2, MidpointRounding.AwayFromZero);
            var kindLabel = kind == PerDiemKind.Overnight ? "overnight" : "day-trip";
            var dayWord = days == 1 ? "day" : "days";
            var description =
                $"Per diem: {days} {dayWord} {kindLabel} @ " +
                $"€{rate.ToString("0.00", CultureInfo.InvariantCulture)} = " +
                $"€{amount.ToString("0.00", CultureInfo.InvariantCulture)}";
            if (!string.IsNullOrWhiteSpace(note))
                description += $" — {note.Trim()}";
            return await AddComputedLineAsync(reportId, submitterUserId, description, amount, ExpenseLineType.PerDiem, ct);
        }, "Error adding per-diem line to report {ReportId}", reportId);

    public Task<ExpenseMutationResult> UpdateLineWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin,
        Guid lineId, string description, decimal amount,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var (editableReport, refusal) = await GetEditableReportAsync(reportId, actorUserId, actorIsFinanceAdmin, ct);
            if (refusal is not null) return refusal;
            var report = editableReport!;

            var existing = report.Lines.FirstOrDefault(l => l.Id == lineId);
            if (existing is null) return ExpenseMutationResult.Failure("Expenses_Validation_LineNotOnReport");
            // Travel lines carry computed amounts and waive the receipt on that basis; a free-text edit would allow an
            // arbitrary unreceipted claim. Remove and re-add to recompute.
            if (existing.LineType is ExpenseLineType.Mileage or ExpenseLineType.PerDiem)
                return ExpenseMutationResult.Failure("Expenses_Validation_TravelLinesComputedCannotEdit");

            if (decimal.Round(amount, 2) != amount)
                return ExpenseMutationResult.Failure("Expenses_Validation_AmountPrecision");

            var line = new ExpenseLine
            {
                Id = lineId,
                ExpenseReportId = reportId,
                Description = description,
                Amount = amount
            };
            var ok = await repo.UpdateLineAsync(reportId, line, ct);
            if (!ok) throw new InvalidOperationException("Failed to update line.");

            await reportAudit.AuditOnBehalfEditAsync(report, actorUserId,
                $"Updated line \"{existing.Description}\" €{existing.Amount} to \"{description}\" €{amount}", ct);

            return ExpenseMutationResult.Success;
        }, "Error updating line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> RemoveLineWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin, Guid lineId,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var (editableReport, refusal) = await GetEditableReportAsync(reportId, actorUserId, actorIsFinanceAdmin, ct);
            if (refusal is not null) return refusal;
            var report = editableReport!;
            // Read the line before it is gone — the audit entry names what was removed, not an id.
            var removed = report.Lines.FirstOrDefault(l => l.Id == lineId);

            var candidates = report.Lines.Where(line => line.Id == lineId || line.ParentLineId == lineId)
                .Select(line => line.Attachment).OfType<ExpenseAttachmentDto>()
                .Select(attachment => (attachment.Id, attachment.Extension)).ToList();
            IReadOnlyList<ExpenseAttachment>? removedAttachments = null;
            try
            {
                // One atomic save removes the line, its proof rows and their attachment metadata.
                removedAttachments = await repo.RemoveLineAsync(reportId, lineId, ct)
                    ?? throw new InvalidOperationException("Failed to remove line.");

                await reportAudit.AuditOnBehalfEditAsync(report, actorUserId,
                    removed is null
                        ? $"Removed line {lineId}"
                        : $"Removed line \"{removed.Description}\" €{removed.Amount}", CancellationToken.None);
                return ExpenseMutationResult.Success;
            }
            finally
            {
                await attachments.CleanupUnlinkedAttachmentsAsync(
                    removedAttachments is null ? candidates : removedAttachments.Select(a => (a.Id, a.Extension)), lineId);
            }
        }, "Error removing line {LineId} from report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> AttachFileToLineWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin,
        Guid lineId, string originalFileName, string contentType,
        Stream content, CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var refusal = await attachments.StoreAttachmentAsync(
                reportAccess, reportId, actorUserId, actorIsFinanceAdmin, lineId, originalFileName, contentType, content, ct);
            return refusal is null ? ExpenseMutationResult.Success : new(false, refusal);
        }, "Error uploading attachment to line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> RemoveAttachmentFromLineAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin,
        Guid lineId, CancellationToken ct = default) =>
        RunMutationAsync(ct,
            () => attachments.RemoveAsync(reportAccess, reportId, actorUserId, actorIsFinanceAdmin, lineId, ct),
            "Error removing attachment from line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> SubmitWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin, CancellationToken ct = default) =>
        RunMutationAsync(ct, () => Transitions.SubmitWithResultAsync(reportId, actorUserId, actorIsFinanceAdmin, ct),
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
        Guid reportId, Guid coordinatorUserId, bool actorIsFinanceAdmin, decimal? maxAmount,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, () => Review.CoordinatorEndorseWithResultAsync(reportId, coordinatorUserId, actorIsFinanceAdmin, maxAmount, ct),
            "Error endorsing expense report {ReportId}", reportId);

    public Task<ExpenseMutationResult> CoordinatorRejectWithResultAsync(
        Guid reportId, Guid coordinatorUserId, bool actorIsFinanceAdmin, string reason,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, () => Review.CoordinatorRejectWithResultAsync(reportId, coordinatorUserId, actorIsFinanceAdmin, reason, ct),
            "Error coordinator-rejecting expense report {ReportId}", reportId);

    public Task<ExpenseMutationResult> DeclineLineWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin, Guid lineId, string reason,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, () => Review.DeclineLineWithResultAsync(reportId, actorUserId, actorIsFinanceAdmin, lineId, reason, ct),
            "Error declining line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> UndoDeclineLineWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin, Guid lineId,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, () => Review.UndoDeclineLineWithResultAsync(reportId, actorUserId, actorIsFinanceAdmin, lineId, ct),
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

    public async Task<IReadOnlyList<Guid>> GetFailedHoldedPushReportIdsAsync(bool actorIsFinanceAdmin, CancellationToken ct = default)
        => actorIsFinanceAdmin ? await repo.GetFailedOutboxReportIdsAsync(ct) : [];

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

    private Task<(ExpenseReportDto? Report, ExpenseMutationResult? Refusal)> GetEditableReportAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin, CancellationToken ct) =>
        reportAccess.GetEditableReportAsync(reportId, actorUserId, actorIsFinanceAdmin, ct);

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
