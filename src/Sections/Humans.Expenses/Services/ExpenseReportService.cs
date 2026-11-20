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
using System.Globalization;
using System.Transactions;
using Humans.Base.Threading;

namespace Humans.Expenses.Services;

internal sealed class ExpenseReportService(
    IExpenseRepository repo,
    IFileStorage fileStorage,
    IBudgetService budgetService,
    IUserService userService,
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

    private readonly TravelReimbursementConfig _travel = travelConfig.Value;

    public Task<ExpenseReportDto?> GetAsync(Guid id, CancellationToken ct = default)
        => repo.GetByIdAsync(id, ct);

    public Task<IReadOnlyList<ExpenseReportDto>> GetAllAsync(CancellationToken ct = default)
        => repo.GetAllAsync(ct);

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

    public async Task<ExpenseCreateDraftResult> CreateDraftAsync(
        Guid submitterUserId, Guid actorUserId, int holdedAccountNumber, string? note,
        Guid? preapprovalGrantId = null,
        CancellationToken ct = default)
    {
        Guid? reportId = null;
        Guid? attemptedId = null;
        var result = await RunMutationAsync(logger, ct, async () =>
        {
            var (resolved, refusal) = preapprovalGrantId is { } grantId
                ? await ResolveGrantBookingAsync(grantId, submitterUserId, heldGrantId: null, ct)
                : await ResolveBookingAsync(holdedAccountNumber, pinnedYearId: null, ct);
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
            if (!await IsSameUserAsync(submitterUserId, actorUserId, CancellationToken.None))
            {
                await auditLogService.LogAsync(
                    ExpensesAuditActions.ExpenseCreatedOnBehalf,
                    AuditEntityTypes.Report, report.Id,
                    $"Created expense report on behalf of {await DescribeMemberAsync(submitterUserId, CancellationToken.None)}."
                    + DescribeGrant(preapprovalGrantId),
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
        RunMutationAsync(logger, ct, () => UpdateDraftWithResultCoreAsync(reportId, actorUserId, DraftEditStatuses, holdedAccountNumber, note, preapprovalGrantId, ct),
            "Error updating expense report {ReportId}", reportId);

    public Task<ExpenseMutationResult> UpdateHeaderForFinanceCorrectionWithResultAsync(
        Guid reportId, Guid actorUserId,
        int holdedAccountNumber, string? note,
        Guid? preapprovalGrantId = null,
        CancellationToken ct = default) =>
        RunMutationAsync(logger, ct, () => UpdateDraftWithResultCoreAsync(reportId, actorUserId, FinanceCorrectionStatuses, holdedAccountNumber, note, preapprovalGrantId, ct),
            "Error updating expense report {ReportId}", reportId);

    public Task<ExpenseAddLineResult> AddLineWithResultAsync(
        Guid reportId, Guid actorUserId,
        string description, decimal amount, ExpenseLineType lineType = ExpenseLineType.Receipt,
        Guid? parentLineId = null, ExpenseFileUpload? file = null, CancellationToken ct = default) =>
        AddLineWithResultCoreAsync(reportId, actorUserId, DraftEditStatuses,
            description, amount, lineType, parentLineId, file, ct);

    public Task<ExpenseAddLineResult> AddLineForFinanceCorrectionWithResultAsync(
        Guid reportId, Guid actorUserId,
        string description, decimal amount, ExpenseLineType lineType = ExpenseLineType.Receipt,
        Guid? parentLineId = null, ExpenseFileUpload? file = null, CancellationToken ct = default) =>
        AddLineWithResultCoreAsync(reportId, actorUserId, FinanceCorrectionStatuses,
            description, amount, lineType, parentLineId, file, ct);

    public Task<ExpenseMutationResult> AddMileageLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        string origin, string destination, decimal km,
        CancellationToken ct = default) =>
        RunMutationAsync(logger, ct, () => AddMileageLineWithResultCoreAsync(reportId, submitterUserId, origin, destination, km, ct),
            "Error adding mileage line to report {ReportId}", reportId);

    public Task<ExpenseMutationResult> AddPerDiemLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        PerDiemKind kind, int days, string? note,
        CancellationToken ct = default) =>
        RunMutationAsync(logger, ct, () => AddPerDiemLineWithResultCoreAsync(reportId, submitterUserId, kind, days, note, ct),
            "Error adding per-diem line to report {ReportId}", reportId);

    public Task<ExpenseMutationResult> UpdateLineWithResultAsync(
        Guid reportId, Guid actorUserId,
        Guid lineId, string description, decimal amount,
        CancellationToken ct = default) =>
        RunMutationAsync(logger, ct, () => UpdateLineWithResultCoreAsync(reportId, actorUserId, DraftEditStatuses, lineId, description, amount, ct),
            "Error updating line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> UpdateLineForFinanceCorrectionWithResultAsync(
        Guid reportId, Guid actorUserId,
        Guid lineId, string description, decimal amount,
        CancellationToken ct = default) =>
        RunMutationAsync(logger, ct, () => UpdateLineWithResultCoreAsync(reportId, actorUserId, FinanceCorrectionStatuses, lineId, description, amount, ct),
            "Error updating line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> RemoveLineWithResultAsync(
        Guid reportId, Guid actorUserId, Guid lineId,
        CancellationToken ct = default) =>
        RunMutationAsync(logger, ct, () => RemoveLineWithResultCoreAsync(reportId, actorUserId, DraftEditStatuses, lineId, ct),
            "Error removing line {LineId} from report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> RemoveLineForFinanceCorrectionWithResultAsync(
        Guid reportId, Guid actorUserId, Guid lineId,
        CancellationToken ct = default) =>
        RunMutationAsync(logger, ct, () => RemoveLineWithResultCoreAsync(reportId, actorUserId, FinanceCorrectionStatuses, lineId, ct),
            "Error removing line {LineId} from report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> AttachFileToLineWithResultAsync(
        Guid reportId, Guid actorUserId,
        Guid lineId, string originalFileName, string contentType,
        Stream content, CancellationToken ct = default) =>
        RunMutationAsync(logger, ct, async () =>
        {
            var refusal = await StoreAttachmentAsync(
                reportId, actorUserId, DraftEditStatuses, lineId, originalFileName, contentType, content, ct);
            return refusal is null ? ExpenseMutationResult.Success : new(false, refusal);
        }, "Error uploading attachment to line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> AttachFileToLineForFinanceCorrectionWithResultAsync(
        Guid reportId, Guid actorUserId,
        Guid lineId, string originalFileName, string contentType,
        Stream content, CancellationToken ct = default) =>
        RunMutationAsync(logger, ct, async () =>
        {
            var refusal = await StoreAttachmentAsync(
                reportId, actorUserId, FinanceCorrectionStatuses, lineId, originalFileName, contentType, content, ct);
            return refusal is null ? ExpenseMutationResult.Success : new(false, refusal);
        }, "Error uploading attachment to line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> RemoveAttachmentFromLineAsync(
        Guid reportId, Guid actorUserId,
        Guid lineId, CancellationToken ct = default) =>
        RunMutationAsync(logger, ct,
            () => RemoveAsync(reportId, actorUserId, DraftEditStatuses, lineId, ct),
            "Error removing attachment from line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> RemoveAttachmentFromLineForFinanceCorrectionAsync(
        Guid reportId, Guid actorUserId,
        Guid lineId, CancellationToken ct = default) =>
        RunMutationAsync(logger, ct,
            () => RemoveAsync(reportId, actorUserId, FinanceCorrectionStatuses, lineId, ct),
            "Error removing attachment from line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> SubmitWithResultAsync(
        Guid reportId, Guid actorUserId, CancellationToken ct = default) =>
        RunMutationAsync(logger, ct, () => SubmitWithResultCoreAsync(reportId, actorUserId, ct),
            "Error submitting expense report {ReportId}", reportId);

    /// <summary>
    /// A member-facing refusal, or null when the claim should be attempted. Mirrors
    /// <see cref="IPreapprovalWrite.ClaimAsync"/>: the report already holding the grant resubmits past the deadline.
    /// </summary>
    public Task<ExpenseMutationResult> WithdrawWithResultAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default) =>
        RunMutationAsync(logger, ct, () => WithdrawWithResultCoreAsync(reportId, submitterUserId, ct),
            "Error withdrawing expense report {ReportId}", reportId);

    /// <summary>Logs a refusal or an unexpected failure; the controller supplies the localized fallback.</summary>
    internal static async Task<ExpenseMutationResult> RunMutationAsync(
        ILogger logger,
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

    internal static string AttachmentKey(Guid id, string extension) =>
        $"uploads/expense-attachments/{id}{extension}";

    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".jpg", ".jpeg", ".png", ".heic"
        };

    public async Task<ExpenseReportDto?> GetReportOwningAttachmentAsync(
        Guid attachmentId, CancellationToken ct = default)
    {
        var reportId = await repo.GetReportIdByAttachmentIdAsync(attachmentId, ct);
        if (reportId is null) return null;
        return await repo.GetByIdAsync(reportId.Value, ct);
    }

    public async Task<ExpenseAttachmentDownload?> TryReadAttachmentAsync(
        ExpenseReportDto owningReport,
        Guid attachmentId,
        CancellationToken ct = default)
    {
        var attachment = owningReport.Lines
            .FirstOrDefault(l => l.Attachment?.Id == attachmentId)?
            .Attachment;
        if (attachment is null) return null;

        var bytes = await fileStorage.TryReadAsync(
            AttachmentKey(attachment.Id, attachment.Extension), ct);
        return bytes is null
            ? null
            : new ExpenseAttachmentDownload(bytes, attachment.ContentType, attachment.OriginalFileName);
    }

    internal async Task CleanupUnlinkedAttachmentsAsync(
        IEnumerable<(Guid Id, string Extension)> attachments, Guid lineId)
    {
        foreach (var attachment in attachments.Distinct())
        {
            try
            {
                // A save can commit before its acknowledgement or audit fails. Verify the actual linkage
                // without caller cancellation; a failed verification retains the file for safety.
                if (await repo.GetReportIdByAttachmentIdAsync(attachment.Id, CancellationToken.None) is not null)
                    continue;
                await fileStorage.DeleteAsync(AttachmentKey(attachment.Id, attachment.Extension), CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not verify or delete attachment file {AttachmentId} for line {LineId}",
                    attachment.Id, lineId);
            }
        }
    }

    private const long AttachmentMaxBytes = 20 * 1024 * 1024;
    private const int AttachmentFileNameMaxLength = 255;

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf", "image/jpeg", "image/jpg", "image/png", "image/heic"
    };

    internal static (string? Extension, ExpenseError? Refusal) ValidateAttachmentUpload(
        string originalFileName, string contentType, Stream content)
    {
        if (content is null || content.Length == 0)
            return (null, ExpenseMutationResult.Failure("Expenses_Flash_SelectFile").Error);
        if (content.Length > AttachmentMaxBytes)
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_FileTooLarge", AttachmentMaxBytes / (1024 * 1024)).Error);

        var fileName = Path.GetFileName(originalFileName);
        if (fileName.Length > AttachmentFileNameMaxLength)
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_FilenameTooLong", AttachmentFileNameMaxLength).Error);

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!AllowedContentTypes.Contains(contentType) || !AllowedExtensions.Contains(extension))
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_UnsupportedFileType").Error);

        return (extension, null);
    }

    internal async Task<ExpenseError?> StoreAttachmentAsync(
        Guid reportId, Guid actorUserId, IReadOnlyCollection<ExpenseReportStatus> allowedStatuses,
        Guid lineId, string originalFileName, string contentType,
        Stream content, CancellationToken ct = default)
    {
        var (fileExtension, uploadRefusal) = ValidateAttachmentUpload(originalFileName, contentType, content);
        if (uploadRefusal is not null) return uploadRefusal;
        var extension = fileExtension!;
        var (editableReport, refusal) = await GetEditableReportAsync(reportId, allowedStatuses, ct);
        if (refusal is not null) return refusal.Error;
        var report = editableReport!;

        var line = report.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null) return new("Expenses_Validation_LineNotOnReport", []);
        var previousAttachment = line.Attachment is { } previous
            ? new ExpenseAttachment
            {
                Id = previous.Id,
                OriginalFileName = previous.OriginalFileName,
                Extension = previous.Extension,
                ContentType = previous.ContentType,
                SizeBytes = previous.SizeBytes,
                UploadedByUserId = previous.UploadedByUserId,
                UploadedAt = previous.UploadedAt,
                HoldedUploadedAt = previous.HoldedUploadedAt,
            }
            : null;

        var attachmentId = Guid.NewGuid();
        await fileStorage.SaveAsync(AttachmentKey(attachmentId, extension), content, ct);

        var attachment = new ExpenseAttachment
        {
            Id = attachmentId,
            OriginalFileName = Path.GetFileName(originalFileName),
            Extension = extension,
            ContentType = contentType,
            SizeBytes = content.Length,
            // The uploader, not the owner: an admin filing on a member's behalf uploads.
            UploadedByUserId = actorUserId,
            UploadedAt = clock.GetCurrentInstant()
        };
        ExpenseAttachment? replacedAttachment = null;
        try
        {
            var replacement = await repo.ReplaceLineAttachmentAsync(reportId, lineId, attachment, ct,
                declineState: line.Declined ? (clock.GetCurrentInstant(), null, null, null) : null);
            if (!replacement.Found)
            {
                // The line disappeared before the save; no metadata was written.
                try
                {
                    await fileStorage.DeleteAsync(AttachmentKey(attachmentId, extension), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Could not delete unlinked attachment file {AttachmentId}", attachmentId);
                }
                return new("Expenses_Validation_LineNotOnReport", []);
            }
            replacedAttachment = replacement.Replaced;

            await auditLogService.LogAsync(
                ExpensesAuditActions.ExpenseAttachmentUploaded,
                AuditEntityTypes.Report, reportId,
                $"Attachment uploaded to line {lineId}.",
                actorUserId,
                relatedEntityId: report.SubmitterUserId,
                relatedEntityType: AuditSubjectTypes.User);
        }
        catch
        {
            try
            {
                // Restore metadata, linkage and any decline/total change atomically, including when the failed
                // save committed before throwing. Keep the old file until audit succeeds.
                await repo.ReplaceLineAttachmentAsync(
                    reportId, lineId, previousAttachment, CancellationToken.None,
                    declineState: line.Declined
                        ? (report.UpdatedAt, line.DeclinedReason, line.DeclinedByUserId, line.DeclinedAt)
                        : null);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Could not roll back attachment metadata {AttachmentId} for line {LineId}",
                    attachmentId, lineId);
            }

            await CleanupUnlinkedAttachmentsAsync([(attachmentId, extension)], lineId);

            throw;
        }

        if (replacedAttachment is not null)
        {
            try
            {
                await fileStorage.DeleteAsync(
                    AttachmentKey(replacedAttachment.Id, replacedAttachment.Extension), CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not delete superseded attachment file {AttachmentId}", replacedAttachment.Id);
            }
        }

        return null;
    }

    internal async Task<ExpenseMutationResult> RemoveAsync(
        Guid reportId, Guid actorUserId, IReadOnlyCollection<ExpenseReportStatus> allowedStatuses,
        Guid lineId, CancellationToken ct)
    {
        var (editableReport, refusal) = await GetEditableReportAsync(reportId, allowedStatuses, ct);
        if (refusal is not null) return refusal;
        var report = editableReport!;

        var line = report.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null)
            return ExpenseMutationResult.Failure("Expenses_Validation_LineNotOnReport");

        if (line.Attachment is null) return ExpenseMutationResult.Success; // idempotent

        ExpenseAttachment? removed = null;
        try
        {
            removed = await repo.RemoveLineAttachmentAsync(reportId, lineId, ct);
            if (removed is null) return ExpenseMutationResult.Success;

            await auditLogService.LogAsync(
                ExpensesAuditActions.ExpenseAttachmentRemoved,
                AuditEntityTypes.Report, reportId,
                $"Attachment removed from line {lineId}.",
                actorUserId,
                relatedEntityId: report.SubmitterUserId,
                relatedEntityType: AuditSubjectTypes.User);
            return ExpenseMutationResult.Success;
        }
        finally
        {
            await CleanupUnlinkedAttachmentsAsync(
                [(removed?.Id ?? line.Attachment.Id, removed?.Extension ?? line.Attachment.Extension)], lineId);
        }
    }

    public async Task<ExpenseHoldedTimeline?> GetHoldedTimelineAsync(
        ExpenseReportDto report, CancellationToken ct = default)
    {
        var outboxEvent = await repo.GetLatestOutboxForReportAsync(report.Id, ct);

        decimal owed = 0m, totalPaid = 0m, memberRegisteredTotal = 0m;
        var paid = false;
        LocalDate? paidOn = null;

        // No contact id means no push has ever linked this member to a Holded creditor, so there is
        // no ledger to read. The push half below still has something to say about why.
        if (!string.IsNullOrEmpty(report.HoldedContactId))
        {
            var status = await finance.GetCreditorStatusAsync(
                report.HoldedSupplierAccountNum, ct);

            // The creditor contact covers every id the human has held, so the total does too.
            var submitter = await userService.GetUserInfoAsync(report.SubmitterUserId, ct);
            foreach (var id in submitter?.AllUserIds ?? [report.SubmitterUserId])
            {
                // Count the documents whatever the report status: reports withdrawn after approval (before that
                // was refused) still have their booking. RegisteredAmount excludes lines not pushed yet.
                memberRegisteredTotal += (await repo.GetForSubmitterAsync(id, ct)).Sum(RegisteredAmount);
            }

            owed = status?.OwedToMember ?? 0m;
            totalPaid = status?.TotalPaid ?? 0m;
            // Settled iff the derived creditor balance (Σdebit − Σcredit) is non-negative. A null status
            // means no cached ledger lines for the account — unknown, not settled.
            paid = status is { } s && s.Balance >= 0m;
            paidOn = status?.LastPaymentDate;
        }

        return new ExpenseHoldedTimeline
        {
            RegisteredInHolded = report.HoldedDocIds.Count > 0,
            OwedToMember = owed,
            MemberRegisteredTotal = memberRegisteredTotal,
            OtherAmount = Math.Max(0m, owed - memberRegisteredTotal),
            Paid = paid,
            PaidOn = paidOn,
            TotalPaid = totalPaid,
            SyncState = ResolveSyncState(outboxEvent),
            QueuedAt = outboxEvent?.OccurredAt,
            SettledAt = outboxEvent?.ProcessedAt,
            RetryCount = outboxEvent?.RetryCount ?? 0,
            MaxRetries = ExpenseReportBackgroundProcessor.MaxOutboxRetries,
            LastError = outboxEvent?.LastError,
            NextRetryAt = outboxEvent is { ProcessedAt: null, FailedPermanently: false }
                ? outboxEvent.NextRetryAt
                : null,
        };
    }

    /// <summary>
    /// A legacy report-level doc carried the whole payable; per-line docs count only lines whose doc exists, so a
    /// half-done push doesn't overstate the registered total against the ledger.
    /// </summary>
    private static decimal RegisteredAmount(ExpenseReportDto report) =>
        report.HoldedDocId is not null
            ? report.Payable
            : PayableAllocation.Allocate(report)
                .Where(a => a.Line.HoldedDocId is not null)
                .Sum(a => a.Booked);

    private ExpenseHoldedSyncState ResolveSyncState(HoldedExpenseOutboxEvent? outboxEvent) =>
        ResolveSyncState(outboxEvent, holded.IsConfigured);

    internal static ExpenseHoldedSyncState ResolveSyncState(
        HoldedExpenseOutboxEvent? outboxEvent, bool holdedConfigured)
    {
        if (outboxEvent is null) return ExpenseHoldedSyncState.NotQueued;
        // Written off sets ProcessedAt too, so it has to be tested before the success case.
        if (outboxEvent.FailedPermanently) return ExpenseHoldedSyncState.Failed;
        if (outboxEvent.ProcessedAt is not null) return ExpenseHoldedSyncState.Pushed;
        if (!holdedConfigured) return ExpenseHoldedSyncState.NotConfigured;
        return outboxEvent.RetryCount > 0
            ? ExpenseHoldedSyncState.Retrying
            : ExpenseHoldedSyncState.Queued;
    }

    public async Task<ExpenseIbanSaveResult> SaveSubmitterIbanWithResultAsync(
        Guid reportId, Guid actorUserId, string? iban, CancellationToken ct = default)
    {
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null)
            return IbanFailure("Expenses_Iban_ReportNotFound", isValidationError: false);
        var submitterUserId = report.SubmitterUserId;

        var ibanValue = string.IsNullOrWhiteSpace(iban) ? null : iban.Trim();

        if (ibanValue is not null && !IbanValidator.IsValid(ibanValue))
            return IbanFailure("Expenses_Iban_InvalidFormat", isValidationError: true);

        var normalized = ibanValue is null ? null : IbanValidator.Normalize(ibanValue);

        // A pending report's payee snapshot is corrected through this page, so clearing is refused there: the snapshot
        // and profile would disagree on who gets paid. A draft has no snapshot yet; an approved report's is history.
        var snapshotIsLive = IsPendingApproval(report.Status);
        if (normalized is null && snapshotIsLive)
            return IbanFailure(
                "Expenses_Iban_RequiredForPendingReport",
                isValidationError: true);

        try
        {
            submitterUserId = (await userService.GetUserInfoAsync(submitterUserId, ct))?.Id ?? submitterUserId;
            var saved = await financeWrite.SetIbanAsync(submitterUserId, normalized, ct);
            if (!saved)
                return IbanFailure("Expenses_Iban_SaveFailed", isValidationError: false);

            if (snapshotIsLive)
                await RefreshPayeeIbanSnapshotAsync(report, actorUserId, normalized!, ct);

            var isClearing = normalized is null;
            // Entity type is Profile and the actor may be somebody else, so without the related id the member's GDPR
            // export would miss the row carrying their raw IBAN.
            await auditLogService.LogAsync(
                isClearing ? ExpensesAuditActions.IbanRemove : ExpensesAuditActions.IbanSet,
                AuditEntityTypes.Profile,
                submitterUserId,
                await DescribeIbanChangeAsync(submitterUserId, actorUserId, normalized, ct),
                actorUserId,
                relatedEntityId: submitterUserId,
                relatedEntityType: AuditSubjectTypes.User);

            logger.LogInformation(
                "IBAN {Action} for user {UserId}",
                isClearing ? "removed" : "set",
                submitterUserId);

            return new ExpenseIbanSaveResult(
                Succeeded: true,
                IsValidationError: false,
                MessageKey: normalized is null ? "Expenses_Iban_Removed" : "Expenses_Iban_Saved");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error setting IBAN for user {UserId}", submitterUserId);
            return IbanFailure("Expenses_Iban_SaveFailed", isValidationError: false);
        }
    }

    private static ExpenseIbanSaveResult IbanFailure(string messageKey, bool isValidationError) =>
        new(Succeeded: false, IsValidationError: isValidationError, MessageKey: messageKey);

    private async Task RefreshPayeeIbanSnapshotAsync(
        ExpenseReportDto report, Guid actorUserId, string normalizedIban, CancellationToken ct)
    {
        if (string.Equals(report.PayeeIban, normalizedIban, StringComparison.Ordinal)) return;

        var updated = await repo.UpdatePayeeIbanAsync(
            report.Id, normalizedIban, clock.GetCurrentInstant(), ct);
        if (!updated) return;

        // On the report so it lands in the history the correcting admin is looking at. Unmasked when set for another
        // member, as above.
        var description = await IsSubmitterAsync(report, actorUserId, ct)
            ? "Payee IBAN updated"
            : $"Payee IBAN updated for {await DescribeMemberAsync(report.SubmitterUserId, ct)} to {normalizedIban}";

        await auditLogService.LogAsync(
            ExpensesAuditActions.ExpensePayeeIbanUpdated,
            AuditEntityTypes.Report, report.Id,
            $"{description}.",
            actorUserId,
            relatedEntityId: report.SubmitterUserId,
            relatedEntityType: AuditSubjectTypes.User);
    }

    /// <summary>
    /// A member's own change stays "IBAN set" / "IBAN removed". When somebody else makes it, the entry names the member
    /// and carries the IBAN <b>unmasked</b>: audit may hold its subject's PII, and only what was typed can trace a
    /// wrong IBAN to its typist. The one exception to masking IBANs.
    /// </summary>
    private async Task<string> DescribeIbanChangeAsync(
        Guid submitterUserId, Guid actorUserId, string? normalizedIban, CancellationToken ct)
    {
        if (await IsSameUserAsync(submitterUserId, actorUserId, ct))
            return normalizedIban is null ? "IBAN removed" : "IBAN set";

        var member = await DescribeMemberAsync(submitterUserId, ct);
        return normalizedIban is null
            ? $"IBAN removed for {member}"
            : $"IBAN set for {member} to {normalizedIban}";
    }

    /// <summary>Requires the command's edit window; the caller authorizes the actor.</summary>
    internal async Task<(ExpenseReportDto? Report, ExpenseMutationResult? Refusal)> GetEditableReportAsync(
        Guid reportId, IReadOnlyCollection<ExpenseReportStatus> allowedStatuses, CancellationToken ct)
    {
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null)
            return (null, ExpenseMutationResult.Failure("Expenses_Iban_ReportNotFound"));
        return allowedStatuses.Contains(report.Status)
            ? (report, null)
            : (null, ExpenseMutationResult.Failure("Expenses_Validation_ReportCannotBeEditedInStatus", report.Status));
    }

    internal Task<bool> IsSubmitterAsync(ExpenseReportDto report, Guid actorUserId, CancellationToken ct) =>
        IsSameUserAsync(report.SubmitterUserId, actorUserId, ct);

    private async Task<bool> IsSameUserAsync(Guid targetUserId, Guid actorUserId, CancellationToken ct) =>
        targetUserId == actorUserId
        || (await userService.GetUserInfoAsync(actorUserId, ct))?.AllUserIds.Contains(targetUserId) == true;

    private async Task<IReadOnlyList<Guid>> AllIdsAsync(Guid userId, CancellationToken ct) =>
        (await userService.GetUserInfoAsync(userId, ct))?.AllUserIds ?? [userId];

    /// <summary>Submitted, not yet approved: has a payee snapshot and a booked year, but is not final.</summary>
    internal static bool IsPendingApproval(ExpenseReportStatus status) =>
        status is ExpenseReportStatus.Submitted or ExpenseReportStatus.CoordinatorEndorsed;

    /// <summary>
    /// The grant's account, with no category or year, which routes the report past endorsement. The grant must be
    /// claimable by the submitter today; the one a draft already holds (<paramref name="heldGrantId"/>) is always kept,
    /// so a rejected report can be corrected after the deadline it met.
    /// </summary>
    internal async Task<(ExpenseBooking? Booking, ExpenseMutationResult? Refusal)> ResolveGrantBookingAsync(
        Guid grantId, Guid submitterUserId, Guid? heldGrantId, CancellationToken ct)
    {
        var grant = grantId == heldGrantId
            ? await PreapprovalGrantQueries.GetGrantAsync(preapprovals, grantId, ct)
            : (await PreapprovalGrantQueries.ListClaimableAsync(preapprovals, await AllIdsAsync(submitterUserId, ct), ct)).FirstOrDefault(g => g.Id == grantId);
        if (grant is null)
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_PreapprovalNotClaimable"));
        return (new ExpenseBooking(grant.HoldedAccountNumber, grant.HoldedAccountId, null, null), null);
    }

    /// <summary>
    /// Resolves what a report books to from the account the submitter picked. A budget-category
    /// account also carries the category it maps to and that category's year; any other account
    /// carries neither, which is what routes the report past coordinator endorsement. A draft takes
    /// the active year's categories; a report past submit (<paramref name="pinnedYearId"/> set)
    /// accepts only its own year's, so a header edit never moves its accounting year.
    /// </summary>
    public async Task<(ExpenseBooking? Booking, ExpenseMutationResult? Refusal)> ResolveBookingAsync(
        int holdedAccountNumber, Guid? pinnedYearId, CancellationToken ct)
    {
        var account = (await finance.ListExpenseAccountsAsync(activeOnly: true, ct))
            .FirstOrDefault(a => a.AccountNum == holdedAccountNumber);
        if (account is null)
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_AccountNotFound"));
        if (!account.IsBudgetCategory)
            return (new ExpenseBooking(account.AccountNum, account.AccountId, null, null), null);

        // The option list said this account is a budget category, so the map row exists; the
        // category behind it may still be gone or belong to another year.
        var mapRow = (await finance.GetCategoryMapAsync(ct))
            .FirstOrDefault(m => m.HoldedAccountNumber == holdedAccountNumber);
        if (mapRow is null)
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_CategoryNotFound"));

        if (pinnedYearId is { } pinned)
        {
            var snapshot = await budgetService.GetCategoryByIdAsync(mapRow.BudgetCategoryId, ct);
            if (snapshot is null)
                return (null, ExpenseMutationResult.Failure("Expenses_Validation_CategoryNotFound"));
            if (snapshot.BudgetGroup?.BudgetYearId != pinned)
                return (null, ExpenseMutationResult.Failure("Expenses_Validation_CategoryDifferentBudgetYear"));
            return (new ExpenseBooking(account.AccountNum, account.AccountId, snapshot.Id, pinned), null);
        }

        var activeYear = await budgetService.GetActiveYearAsync(ct)
            ?? throw new InvalidOperationException("No active budget year.");
        var inActiveYear = activeYear.Groups.SelectMany(g => g.Categories).Any(c => c.Id == mapRow.BudgetCategoryId);
        if (!inActiveYear)
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_CategoryNotFound"));
        return (new ExpenseBooking(account.AccountNum, account.AccountId, mapRow.BudgetCategoryId, activeYear.Id), null);
    }

    /// <summary>Best-effort display name for an audit of an already committed edit.</summary>
    internal async Task<string> DescribeMemberAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            return (await userService.GetUserInfoAsync(userId, ct))?.BurnerName ?? userId.ToString();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogWarning("Audit display-name lookup cancelled for member {UserId}; using member ID", userId);
        }
        catch (Exception ex)
        {
            // Name enrichment must not skip the audit or fail an edit that already committed.
            logger.LogError(ex, "Could not resolve audit display name for member {UserId}; using member ID", userId);
        }
        return userId.ToString();
    }

    /// <summary>
    /// An edit to somebody else's report owes them a trail naming both; a member editing their own leaves none.
    /// <paramref name="whatChanged"/> is the sentence opener, e.g. "Added line 'Fuel'".
    /// </summary>
    internal async Task AuditOnBehalfEditAsync(
        ExpenseReportDto report, Guid actorUserId, string whatChanged, CancellationToken ct)
    {
        if (await IsSubmitterAsync(report, actorUserId, ct)) return;

        await auditLogService.LogAsync(
            ExpensesAuditActions.ExpenseEditedOnBehalf,
            AuditEntityTypes.Report, report.Id,
            $"{whatChanged} on behalf of {await DescribeMemberAsync(report.SubmitterUserId, ct)}.",
            actorUserId,
            relatedEntityId: report.SubmitterUserId,
            relatedEntityType: AuditSubjectTypes.User);
    }

    internal static string MaxAmountDetail(decimal? maxAmount) =>
        maxAmount is { } cap
            ? $" Authorized maximum {cap.ToString("0.00", CultureInfo.InvariantCulture)} EUR."
            : "";

    internal static string DescribeGrant(Guid? grantId) =>
        grantId is { } id ? $" Filed against pre-approval {id}." : "";

    internal static string DescribeNote(string? note) =>
        string.IsNullOrWhiteSpace(note) ? "cleared" : $"\"{note}\"";

    internal const string ExpenseReports = "ExpenseReports";
    internal const string ExpenseAuditLog = "ExpenseAuditLog";

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(
        Guid userId, CancellationToken ct)
    {
        // Asked with an archived id, the survivor's own reports count too.
        var user = await userService.GetUserInfoAsync(userId, ct);
        var allIds = user?.AllUserIds ?? [userId];

        var allReports = new List<ExpenseReportDto>();
        foreach (var id in allIds)
        {
            var reports = await repo.GetForSubmitterAsync(id, ct);
            allReports.AddRange(reports);
        }

        var expenseActions = new List<AuditActionDefinition>
        {
            ExpensesAuditActions.ExpenseCreatedOnBehalf,
            ExpensesAuditActions.ExpenseEditedOnBehalf,
            ExpensesAuditActions.ExpensePayeeIbanUpdated,
            ExpensesAuditActions.ExpenseSubmit,
            ExpensesAuditActions.ExpenseEndorse,
            ExpensesAuditActions.ExpenseCoordinatorReject,
            ExpensesAuditActions.ExpenseApprove,
            ExpensesAuditActions.ExpenseReject,
            ExpensesAuditActions.ExpenseWithdraw,
            ExpensesAuditActions.ExpenseCategoryOverride,
            ExpensesAuditActions.ExpenseSepaSent,
            ExpensesAuditActions.ExpenseSepaReopened,
            ExpensesAuditActions.ExpensePaid,
            ExpensesAuditActions.ExpenseAttachmentUploaded,
            ExpensesAuditActions.ExpenseAttachmentRemoved,
            ExpensesAuditActions.ExpenseLineDeclined,
            ExpensesAuditActions.ExpenseLineDeclineUndone,
            ExpensesAuditActions.IbanSet,
            ExpensesAuditActions.IbanRemove,
            FinanceAuditActions.IbanReveal,
        }.Select(a => a.Name).ToList();

        var auditEntries = (await auditViewer.GetFilteredAsync(
            entityType: null,
            entityId: null,
            userId: userId,
            actions: expenseActions,
            limit: 10_000,
            ct: ct)).ToList();

        // Decisions others took on the member's claims name the claim, not the member.
        foreach (var report in allReports)
        {
            auditEntries.AddRange(await auditViewer.GetFilteredAsync(
                entityType: AuditEntityTypes.Report,
                entityId: report.Id,
                userId: null,
                actions: expenseActions,
                limit: 10_000,
                ct: ct));
        }

        var allIdSet = allIds.ToHashSet();
        var reportIds = allReports.Select(r => r.Id).ToHashSet();
        auditEntries = auditEntries
            .Where(e => IsSubjectAuditEntry(e, allIdSet, reportIds))
            .DistinctBy(e => e.Id)
            .OrderByDescending(e => e.OccurredAt)
            .ToList();

        var shapedReports = allReports
            .OrderBy(r => r.CreatedAt)
            .Select(r => new
            {
                r.Id,
                r.Status,
                r.Note,
                r.PayeeName,
                PayeeIban = IbanFormatter.Mask(r.PayeeIban),
                r.Total,
                r.MaxAmount,
                SubmittedAt = r.SubmittedAt?.ToIso8601(),
                CoordinatorEndorsedAt = r.CoordinatorEndorsedAt?.ToIso8601(),
                ApprovedAt = r.ApprovedAt?.ToIso8601(),
                r.LastRejectionReason,
                LastRejectedAt = r.LastRejectedAt?.ToIso8601(),
                CreatedAt = r.CreatedAt.ToIso8601(),
                Lines = r.Lines.Select(l => new
                {
                    l.Id,
                    l.Description,
                    l.Amount,
                    l.LineType,
                    l.ParentLineId,
                    l.SortOrder,
                    l.Declined,
                    l.DeclinedReason,
                    DeclinedAt = l.DeclinedAt?.ToIso8601(),
                    Attachment = l.Attachment is null
                        ? null
                        : new
                        {
                            l.Attachment.OriginalFileName,
                            l.Attachment.ContentType,
                            l.Attachment.SizeBytes,
                        }
                }).ToList()
            }).ToList();

        var shapedAudit = auditEntries
            .Select(e => new
            {
                Action = e.Action.Name,
                e.EntityType,
                e.EntityId,
                Description = IbanFormatter.MaskAllIn(e.Description),
                OccurredAt = e.OccurredAt.ToIso8601()
            }).ToList();

        return
        [
            new UserDataSlice(ExpenseReports,
                shapedReports.Count > 0 ? shapedReports : null),
            new UserDataSlice(ExpenseAuditLog,
                shapedAudit.Count > 0
                    ? new { Entries = shapedAudit }
                    : (object?)null),
        ];
    }

    private static bool IsSubjectAuditEntry(
        AuditEvent entry, HashSet<Guid> userIds, HashSet<Guid> reportIds) =>
        (entry.RelatedEntityId is { } relatedId
            && (entry.RelatedEntityType is null
                || string.Equals(entry.RelatedEntityType, AuditSubjectTypes.User, StringComparison.Ordinal))
            && userIds.Contains(relatedId))
        || (string.Equals(entry.EntityType, AuditSubjectTypes.User, StringComparison.Ordinal)
            && userIds.Contains(entry.EntityId))
        || (string.Equals(entry.EntityType, AuditEntityTypes.Profile, StringComparison.Ordinal)
            && userIds.Contains(entry.EntityId))
        || (string.Equals(entry.EntityType, AuditEntityTypes.Report, StringComparison.Ordinal)
            && reportIds.Contains(entry.EntityId));

    private async Task<(ExpenseAddLineResult Result, ExpenseReportDto? Report)> AddLineWithoutAuditAsync(
        Guid reportId, IReadOnlyCollection<ExpenseReportStatus> allowedStatuses,
        string description, decimal amount, ExpenseLineType lineType,
        Guid? parentLineId, CancellationToken ct)
    {
        var (editableReport, refusal) = await GetEditableReportAsync(reportId, allowedStatuses, ct);
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

    internal async Task<ExpenseAddLineResult> AddLineWithResultCoreAsync(
        Guid reportId, Guid actorUserId, IReadOnlyCollection<ExpenseReportStatus> allowedStatuses,
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
            if (file is not null && ExpenseReportService.ValidateAttachmentUpload(file.FileName, file.ContentType, file.Content).Refusal is { } uploadRefusal)
                return Reject(uploadRefusal);

            var (added, report) = await AddLineWithoutAuditAsync(
                reportId, allowedStatuses,
                description, amount, lineType, parentLineId, ct);
            if (added.Error is { } addRefusal) return Reject(addRefusal);
            var lineId = added.LineId!.Value;
            ExpenseError? attachmentRefusal = null;
            try
            {
                if (file is not null)
                    attachmentRefusal = await StoreAttachmentAsync(
                        reportId, actorUserId, allowedStatuses, lineId, file.FileName, file.ContentType, file.Content, ct);
                if (attachmentRefusal is null)
                    await AuditOnBehalfEditAsync(report!, actorUserId,
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
                await CleanupUnlinkedAttachmentsAsync(
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
            reportId, [ExpenseReportStatus.Draft], description, amount, lineType, null, ct);
        if (added.Error is not null) return new(false, added.Error);
        await AuditOnBehalfEditAsync(report!, actorUserId, $"Added line \"{description}\" €{amount}", ct);
        return ExpenseMutationResult.Success;
    }

    internal async Task<ExpenseMutationResult> AddMileageLineWithResultCoreAsync(
        Guid reportId, Guid submitterUserId,
        string origin, string destination, decimal km,
        CancellationToken ct = default)
    {
        var rate = _travel.MileageRatePerKm;
        var amount = Math.Round(km * rate, 2, MidpointRounding.AwayFromZero);
        var description =
            $"{origin.Trim()} to {destination.Trim()}, " +
            $"{km.ToString("0.#", CultureInfo.InvariantCulture)} km @ " +
            $"€{rate.ToString("0.00", CultureInfo.InvariantCulture)} = " +
            $"€{amount.ToString("0.00", CultureInfo.InvariantCulture)}";
        return await AddComputedLineAsync(reportId, submitterUserId, description, amount, ExpenseLineType.Mileage, ct);
    }

    internal async Task<ExpenseMutationResult> AddPerDiemLineWithResultCoreAsync(
        Guid reportId, Guid submitterUserId,
        PerDiemKind kind, int days, string? note,
        CancellationToken ct = default)
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
    }

    internal async Task<ExpenseMutationResult> UpdateLineWithResultCoreAsync(
        Guid reportId, Guid actorUserId, IReadOnlyCollection<ExpenseReportStatus> allowedStatuses,
        Guid lineId, string description, decimal amount,
        CancellationToken ct = default)
    {
        var (editableReport, refusal) = await GetEditableReportAsync(reportId, allowedStatuses, ct);
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

        await AuditOnBehalfEditAsync(report, actorUserId,
            $"Updated line \"{existing.Description}\" €{existing.Amount} to \"{description}\" €{amount}", ct);

        return ExpenseMutationResult.Success;
    }

    internal async Task<ExpenseMutationResult> RemoveLineWithResultCoreAsync(
        Guid reportId, Guid actorUserId, IReadOnlyCollection<ExpenseReportStatus> allowedStatuses, Guid lineId,
        CancellationToken ct = default)
    {
        var (editableReport, refusal) = await GetEditableReportAsync(reportId, allowedStatuses, ct);
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

            await AuditOnBehalfEditAsync(report, actorUserId,
                removed is null
                    ? $"Removed line {lineId}"
                    : $"Removed line \"{removed.Description}\" €{removed.Amount}", CancellationToken.None);
            return ExpenseMutationResult.Success;
        }
        finally
        {
            await CleanupUnlinkedAttachmentsAsync(
                removedAttachments is null ? candidates : removedAttachments.Select(a => (a.Id, a.Extension)), lineId);
        }
    }

    // Scoped transitions hold this through ambient transaction disposal and completion. Shared with
    // ExpenseReviewService: submit, withdraw, header edits and approval must not interleave.
    internal static readonly TrackedLock TransitionLock = new("Expenses.ReportTransitions");

    internal async Task<ExpenseMutationResult> UpdateDraftWithResultCoreAsync(
        Guid reportId, Guid actorUserId, IReadOnlyCollection<ExpenseReportStatus> allowedStatuses,
        int holdedAccountNumber, string? note,
        Guid? preapprovalGrantId = null,
        CancellationToken ct = default)
    {
        using var transition = await TransitionLock.AcquireAsync(logger, ct);
        var (editableReport, refusal) = await GetEditableReportAsync(reportId, allowedStatuses, ct);
        if (refusal is not null) return refusal;
        var report = editableReport!;

        ExpenseBooking booking;
        if (report.Status != ExpenseReportStatus.Draft
            && (preapprovalGrantId != report.PreapprovalGrantId
                || (report.PreapprovalGrantId is not null && holdedAccountNumber != report.HoldedAccountNumber)))
        {
            // The grant was claimed at submit and the report stamped from it; neither moves until
            // the report leaves the pending window (withdraw releases, approval consumes).
            return ExpenseMutationResult.Failure("Expenses_Validation_PreapprovalLocked");
        }
        if (report.Status != ExpenseReportStatus.Draft && report.PreapprovalGrantId is not null)
        {
            // A program edit must not rebook a submitted report during an unrelated header edit.
            booking = new ExpenseBooking(report.HoldedAccountNumber, report.HoldedAccountId,
                report.BudgetCategoryId, report.BudgetYearId);
        }
        else if (preapprovalGrantId is { } grantId)
        {
            var (grantBooking, grantRefusal) = await ResolveGrantBookingAsync(
                grantId, report.SubmitterUserId, report.PreapprovalGrantId, ct);
            if (grantRefusal is not null) return grantRefusal;
            booking = grantBooking!;
        }
        else
        {
            // A draft resolves through the active year. A submitted report already belongs to a year;
            // re-resolving it would silently move last year's accounting into this year's books.
            var pinnedYearId = ExpenseReportService.IsPendingApproval(report.Status) ? report.BudgetYearId : null;
            var (resolved, bookingRefusal) = await ResolveBookingAsync(holdedAccountNumber, pinnedYearId, ct);
            if (bookingRefusal is not null) return bookingRefusal;
            booking = resolved!;
        }

        var updated = new ExpenseReport
        {
            Id = reportId,
            HoldedAccountNumber = booking.HoldedAccountNumber,
            HoldedAccountId = booking.HoldedAccountId,
            BudgetCategoryId = booking.BudgetCategoryId,
            BudgetYearId = booking.BudgetYearId,
            PreapprovalGrantId = preapprovalGrantId,
            Note = note,
            UpdatedAt = clock.GetCurrentInstant()
        };

        // A rejected draft still holds (Claimed) the grant it was submitted against. Dropping or swapping it
        // releases that claim in the same transaction. A grant revoked meanwhile is left behind.
        var released = report.PreapprovalGrantId is { } held && held != preapprovalGrantId
            && (await PreapprovalGrantQueries.GetGrantAsync(preapprovals, held, ct))?.Status == GrantStatus.Claimed
            ? held
            : (Guid?)null;
        if (released is { } releasedId)
        {
            var afterCommit = new List<Func<CancellationToken, Task>>();
            using (var scope = new TransactionScope(
                TransactionScopeOption.Required,
                new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
                TransactionScopeAsyncFlowOption.Enabled))
            {
                var release = await preapprovalWrites.ReleaseAsync(releasedId, reportId, actorUserId, ct, afterCommit.Add);
                if (release.Refusal is { } releaseRefusal)
                {
                    logger.LogWarning("Pre-approval {GrantId} refused release by report {ReportId}: {Reason}", releasedId, reportId, releaseRefusal);
                    return ExpenseMutationResult.Failure("Expenses_Validation_PreapprovalUnavailable");
                }
                await repo.UpdateDraftAsync(updated, ct);
                scope.Complete();
            }
            foreach (var complete in afterCommit) await complete(CancellationToken.None);
        }
        else
        {
            await repo.UpdateDraftAsync(updated, ct);
        }

        await AuditOnBehalfEditAsync(report, actorUserId,
            $"Updated header (account {booking.HoldedAccountNumber}, subject {ExpenseReportService.DescribeNote(note)})"
            + (preapprovalGrantId != report.PreapprovalGrantId
                ? $" and pre-approval ({report.PreapprovalGrantId?.ToString() ?? "none"} → {preapprovalGrantId?.ToString() ?? "none"})"
                : ""), ct);

        return ExpenseMutationResult.Success;
    }

    internal async Task<ExpenseMutationResult> SubmitWithResultCoreAsync(
        Guid reportId, Guid actorUserId, CancellationToken ct = default)
    {
        using var transition = await TransitionLock.AcquireAsync(logger, ct);
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return ExpenseMutationResult.Failure("Expenses_Validation_CouldNotSubmitReport");
        if (report.Status != ExpenseReportStatus.Draft) return ExpenseMutationResult.Failure("Expenses_Validation_CouldNotSubmitReport");

        // The Subject carries the project name; reviewers cannot tell what was paid for without it.
        if (string.IsNullOrWhiteSpace(report.Note))
            return ExpenseMutationResult.Failure("Expenses_Validation_SubjectRequired");

        if (!report.Lines.Any())
            return ExpenseMutationResult.Failure("Expenses_Validation_ReportNeedsLine");

        // Receipt lines (proof rows included) need their receipt; invoice lines need the invoice file.
        if (report.Lines.Any(l => l.LineType is ExpenseLineType.Receipt or ExpenseLineType.Invoice
                                  && l.AttachmentId is null))
            return ExpenseMutationResult.Failure("Expenses_Validation_ReceiptInvoiceNeedAttachment");

        // The payee is the report's owner, never whoever pressed Submit, or an on-behalf submit pays the wrong
        // account.
        var submitter = await userService.GetUserInfoAsync(report.SubmitterUserId, ct);
        if (await finance.GetIbanAsync(report.SubmitterUserId, ct) is not { Length: > 0 } payeeIban)
            return ExpenseMutationResult.Failure("Expenses_Validation_SubmitterNeedsIban");

        // Financial records use the legal name, not BurnerName.
        if (string.IsNullOrWhiteSpace(submitter?.FirstName) || string.IsNullOrWhiteSpace(submitter.LastName))
        {
            return ExpenseMutationResult.Failure("Expenses_Validation_SubmitterNeedsFirstAndLastName");
        }
        var legalName = submitter.FullName;

        var now = clock.GetCurrentInstant();
        if (report.PreapprovalGrantId is not { } grantId)
        {
            var ok = await repo.SubmitAsync(reportId, legalName, payeeIban, now, ct: ct);
            if (!ok) return ExpenseMutationResult.Failure("Expenses_Validation_CouldNotSubmitReport");
        }
        else
        {
            // The claim pre-check only picks the member's message; ClaimAsync is the gate, and it
            // runs first inside the transaction so a refused claim leaves the report untouched.
            var grantRefusal = await GetGrantClaimRefusalAsync(grantId, reportId, ct);
            if (grantRefusal is not null) return grantRefusal;

            var afterCommit = new List<Func<CancellationToken, Task>>();
            using (var scope = new TransactionScope(
                TransactionScopeOption.Required,
                new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
                TransactionScopeAsyncFlowOption.Enabled))
            {
                var claim = await preapprovalWrites.ClaimAsync(grantId, reportId, actorUserId, ct, afterCommit.Add);
                if (claim.Refusal is { } refusal)
                {
                    logger.LogWarning("Pre-approval {GrantId} refused the claim by report {ReportId}: {Reason}", grantId, reportId, refusal);
                    return ExpenseMutationResult.Failure("Expenses_Validation_PreapprovalUnavailable");
                }
                var stamp = claim.Claimed!;
                var ok = await repo.SubmitAsync(
                    reportId, legalName, payeeIban, now,
                    grantStamp: new ExpenseBooking(stamp.HoldedAccountNumber, stamp.HoldedAccountId, null, null),
                    grantMaxAmount: stamp.MaxAmount,
                    ct: ct);
                if (!ok) return ExpenseMutationResult.Failure("Expenses_Validation_CouldNotSubmitReport");
                scope.Complete();
            }
            foreach (var complete in afterCommit) await complete(CancellationToken.None);
        }

        await auditLogService.LogAsync(
            ExpensesAuditActions.ExpenseSubmit,
            AuditEntityTypes.Report, reportId,
            (await IsSubmitterAsync(report, actorUserId, ct)
                ? "Submitted expense report."
                : $"Submitted expense report on behalf of {await DescribeMemberAsync(report.SubmitterUserId, ct)}.")
            + ExpenseReportService.DescribeGrant(report.PreapprovalGrantId),
            actorUserId,
            relatedEntityId: report.SubmitterUserId,
            relatedEntityType: AuditSubjectTypes.User);

        return ExpenseMutationResult.Success;
    }

    internal async Task<ExpenseMutationResult> WithdrawWithResultCoreAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default)
    {
        using var transition = await TransitionLock.AcquireAsync(logger, ct);
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return ExpenseMutationResult.Failure("Expenses_Flash_WithdrawFailed");
        if (!await IsSubmitterAsync(report, submitterUserId, ct))
            return ExpenseMutationResult.Failure("Expenses_Validation_OnlySubmitterCanWithdraw");

        // Approval queues the Holded push, so only a report still pending approval can be
        // withdrawn; the repository refuses anything else too.
        if (!ExpenseReportService.IsPendingApproval(report.Status))
            return ExpenseMutationResult.Failure("Expenses_Flash_WithdrawFailed");

        var now = clock.GetCurrentInstant();
        // A held pre-approval goes back to the member. A grant revoked while pending is no longer held and is left
        // alone, so the withdrawal still goes through.
        var heldGrant = report.PreapprovalGrantId is { } grantId
            ? await PreapprovalGrantQueries.GetGrantAsync(preapprovals, grantId, ct)
            : null;
        var releasesGrant = heldGrant?.Status == GrantStatus.Claimed;
        if (!releasesGrant)
        {
            var ok = await repo.WithdrawAsync(reportId, now, ct);
            if (!ok) return ExpenseMutationResult.Failure("Expenses_Flash_WithdrawFailed");
        }
        else
        {
            var afterCommit = new List<Func<CancellationToken, Task>>();
            using (var scope = new TransactionScope(
                TransactionScopeOption.Required,
                new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
                TransactionScopeAsyncFlowOption.Enabled))
            {
                var release = await preapprovalWrites.ReleaseAsync(report.PreapprovalGrantId!.Value, reportId, submitterUserId, ct, afterCommit.Add);
                if (release.Refusal is { } releaseRefusal)
                {
                    logger.LogWarning("Pre-approval {GrantId} refused release by report {ReportId}: {Reason}", report.PreapprovalGrantId, reportId, releaseRefusal);
                    return ExpenseMutationResult.Failure("Expenses_Flash_WithdrawFailed");
                }
                var ok = await repo.WithdrawAsync(reportId, now, ct);
                if (!ok) return ExpenseMutationResult.Failure("Expenses_Flash_WithdrawFailed");
                scope.Complete();
            }
            foreach (var complete in afterCommit) await complete(CancellationToken.None);
        }

        await auditLogService.LogAsync(
            ExpensesAuditActions.ExpenseWithdraw,
            AuditEntityTypes.Report, reportId,
            "Withdrew expense report."
            + (releasesGrant
                ? $" Released pre-approval {report.PreapprovalGrantId}."
                : heldGrant is not null ? $" Pre-approval {report.PreapprovalGrantId} is {heldGrant.Status}; left as is." : ""),
            submitterUserId);

        return ExpenseMutationResult.Success;
    }

    private async Task<ExpenseMutationResult?> GetGrantClaimRefusalAsync(Guid grantId, Guid reportId, CancellationToken ct)
    {
        var grant = await PreapprovalGrantQueries.GetGrantAsync(preapprovals, grantId, ct);
        switch (grant?.Status)
        {
            case GrantStatus.Revoked:
                return ExpenseMutationResult.Failure("Expenses_Validation_PreapprovalRevoked");
            case GrantStatus.Open:
                return grant.ClaimableToday
                    ? null
                    : ExpenseMutationResult.Failure("Expenses_Validation_PreapprovalDeadlinePassed", grant.ClaimBy.ToInvariantDate());
            case GrantStatus.Claimed:
                // Held by this report (a resubmit) or by another one; ClaimAsync tells them apart.
                return null;
            default:
                return ExpenseMutationResult.Failure("Expenses_Validation_PreapprovalUnavailable");
        }
    }
}
