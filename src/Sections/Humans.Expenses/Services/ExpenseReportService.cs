using Humans.Base.Attributes;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Budget.Contracts;
using Humans.Email.Contracts;
using Humans.Finance.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Holded.Contracts;
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

/// <summary>
/// Expenses' application service: the report state machine over the section's repository,
/// plus the Holded outbox drain and the GDPR export contributor.
/// </summary>
[CrossSectionWrite("Writes the reimbursement IBAN onto the claimant profile.")]
internal sealed class ExpenseReportService(
    IExpenseRepository repo,
    IFileStorage fileStorage,
    IBudgetServiceRead budgetService,
    ITeamServiceRead teamService,
    IUserService userService,
    IUserEmailService userEmailService,
    IEmailService emailService,
    ExpensesEmails emails,
    IAuditLogService auditLogService,
    IHoldedClient holdedClient,
    IHoldedFinanceService holdedFinance,
    IClock clock,
    ILogger<ExpenseReportService> logger,
    IOptions<TravelReimbursementConfig> travelConfig) : IExpenseReportService,
        IExpenseReportBackgroundProcessor, IUserDataContributor
{
    internal const string ExpenseReports = "ExpenseReports";
    internal const string ExpenseAuditLog = "ExpenseAuditLog";

    private readonly ExpenseHoldedPublisher holdedPublisher = new(
        repo, fileStorage, userService, holdedClient, holdedFinance, auditLogService, logger);

    private readonly TravelReimbursementConfig _travel = travelConfig.Value;

    /// <summary>
    /// Attempts a Holded push gets before it is written off. With the 2^n-minute backoff below,
    /// ten attempts span roughly 17 hours — long enough to ride out a Holded outage, short enough
    /// that a genuinely broken push surfaces on /Expenses/Review the same day.
    /// </summary>
    private const int MaxOutboxRetries = 10;

    /// <summary>Audit actor for pushes, which run unattended. Matches the Hangfire job's type name.</summary>
    internal const string OutboxJobName = "HoldedExpenseOutboxJob";

    internal static string AttachmentKey(Guid id, string extension) =>
        $"uploads/expense-attachments/{id}{extension}";

    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".jpg", ".jpeg", ".png", ".heic"
        };

    public Task<ExpenseReportDto?> GetAsync(Guid id, CancellationToken ct = default)
        => repo.GetByIdAsync(id, ct);

    public Task<IReadOnlyList<ExpenseReportDto>> GetAllAsync(CancellationToken ct = default)
        => repo.GetAllAsync(ct);

    /// <summary>
    /// Two halves of the same round-trip. The payment half aggregates the submitter's owed/paid
    /// position from the cached Holded creditor balance — the balance already sums all of a member's
    /// outstanding docs, so when it exceeds their own registered-unpaid ER totals the remainder shows
    /// as fronted/adjustments (spec §3). The push half reports where this report's outbox event
    /// stands, so a finance admin can tell a queued push from a written-off one
    /// (nobodies-collective/Humans#1045).
    /// </summary>
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
            var status = await holdedFinance.GetCreditorStatusAsync(
                report.HoldedSupplierAccountNum, ct);

            var memberReports = await repo.GetForSubmitterAsync(report.SubmitterUserId, ct);
            // Withdrawal does not undo a Holded booking. Count the actual documents regardless of
            // report status; RegisteredAmount excludes lines that have not been pushed yet.
            memberRegisteredTotal = memberReports.Sum(RegisteredAmount);

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
            MaxRetries = MaxOutboxRetries,
            LastError = outboxEvent?.LastError,
            NextRetryAt = outboxEvent is { ProcessedAt: null, FailedPermanently: false }
                ? outboxEvent.NextRetryAt
                : null,
        };
    }

    /// <summary>
    /// What of this report is actually booked in Holded right now. A legacy report-level doc always
    /// carried the whole payable; per-line docs count only the lines whose doc exists, so a push that
    /// failed partway (retrying) doesn't overstate the member's registered total against the ledger.
    /// </summary>
    private static decimal RegisteredAmount(ExpenseReportDto report) =>
        report.HoldedDocId is not null
            ? report.Payable
            : PayableAllocation.Allocate(report)
                .Where(a => a.Line.HoldedDocId is not null)
                .Sum(a => a.Booked);

    private ExpenseHoldedSyncState ResolveSyncState(HoldedExpenseOutboxEvent? outboxEvent)
    {
        if (outboxEvent is null) return ExpenseHoldedSyncState.NotQueued;
        // Written off sets ProcessedAt too, so it has to be tested before the success case.
        if (outboxEvent.FailedPermanently) return ExpenseHoldedSyncState.Failed;
        if (outboxEvent.ProcessedAt is not null) return ExpenseHoldedSyncState.Pushed;
        if (!holdedClient.IsConfigured) return ExpenseHoldedSyncState.NotConfigured;
        return outboxEvent.RetryCount > 0
            ? ExpenseHoldedSyncState.Retrying
            : ExpenseHoldedSyncState.Queued;
    }

    public Task<IReadOnlyList<ExpenseReportDto>> GetForSubmitterAsync(
        Guid submitterUserId, CancellationToken ct = default)
        => repo.GetForSubmitterAsync(submitterUserId, ct);

    public async Task<IReadOnlyList<ExpenseReportDto>> GetReviewQueueAsync(
        Guid viewerUserId, bool isFinanceAdmin, CancellationToken ct = default)
    {
        var queue = await repo.GetForReviewQueueAsync(ct);
        if (isFinanceAdmin) return queue;

        // One queue, three audiences (peterdrier/Humans#1447). Filtering the whole queue in
        // memory beats a per-audience query at our small scale, and keeps the ordering the repo
        // already chose. Drafts and withdrawals are excluded upstream for everyone.
        var categoryIds = await GetCoordinatorCategoryIdsAsync(viewerUserId, ct);
        return queue
            .Where(r => r.SubmitterUserId == viewerUserId || categoryIds.Contains(r.BudgetCategoryId))
            .ToList();
    }

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

    public async Task<IReadOnlyList<ExpenseReportDto>> GetCoordinatorQueueAsync(
        Guid coordinatorUserId, CancellationToken ct = default)
    {
        var categoryIds = await GetCoordinatorCategoryIdsAsync(coordinatorUserId, ct);
        if (categoryIds.Count == 0) return [];

        return await repo.GetByCategoryIdsAndStatusAsync(categoryIds,
            ExpenseReportStatus.Submitted, ct);
    }

    private async Task<IReadOnlyList<Guid>> GetCoordinatorCategoryIdsAsync(
        Guid coordinatorUserId, CancellationToken ct)
    {
        var teamIds = await teamService.GetEffectiveBudgetCoordinatorTeamIdsAsync(coordinatorUserId, ct);
        if (teamIds.Count == 0) return [];

        var year = await budgetService.GetActiveYearAsync();
        if (year is null) return [];

        return year.Groups
            .SelectMany(g => g.Categories)
            .Where(c => c.TeamId is { } teamId && teamIds.Contains(teamId))
            .Select(c => c.Id)
            .ToList();
    }

    public async Task<Guid> CreateDraftAsync(
        Guid submitterUserId, Guid actorUserId, Guid budgetCategoryId, string? note,
        CancellationToken ct = default)
    {
        var year = await budgetService.GetActiveYearAsync()
            ?? throw new InvalidOperationException("No active budget year.");
        var category = year.Groups.SelectMany(g => g.Categories)
            .FirstOrDefault(c => c.Id == budgetCategoryId)
            ?? throw new InvalidOperationException("Category not in active year.");

        var now = clock.GetCurrentInstant();
        var report = new ExpenseReport
        {
            Id = Guid.NewGuid(),
            SubmitterUserId = submitterUserId,
            BudgetCategoryId = category.Id,
            BudgetYearId = year.Id,
            Status = ExpenseReportStatus.Draft,
            Note = note,
            PayeeName = "",
            PayeeIban = "",
            Total = 0m,
            CreatedAt = now,
            UpdatedAt = now
        };
        await repo.AddDraftAsync(report, ct);

        // Self-created drafts stay unaudited — the report itself is the record. A report filed for
        // somebody else is an action taken on their behalf, so it leaves a trail naming both.
        if (actorUserId != submitterUserId)
        {
            await auditLogService.LogAsync(
                AuditAction.ExpenseCreatedOnBehalf,
                AuditEntityTypes.Report, report.Id,
                $"Created expense report on behalf of {await DescribeMemberAsync(submitterUserId, ct)}.",
                actorUserId,
                relatedEntityId: submitterUserId,
                relatedEntityType: AuditEntityTypes.User);
        }

        return report.Id;
    }

    /// <summary>How a member is named in an audit description written by somebody else.</summary>
    private async Task<string> DescribeMemberAsync(Guid userId, CancellationToken ct) =>
        (await userService.GetUserInfoAsync(userId, ct))?.BurnerName ?? userId.ToString();

    /// <summary>
    /// Records an edit an admin made to somebody else's report. A member editing their own leaves
    /// no entry — the report is its own record — but an action taken on a member's behalf owes them
    /// a trail naming both, so every header and line change writes one when the actor is not the
    /// submitter. <paramref name="whatChanged"/> is the sentence opener, e.g. "Added line 'Fuel'".
    /// </summary>
    private async Task AuditOnBehalfEditAsync(
        ExpenseReportDto report, Guid actorUserId, string whatChanged, CancellationToken ct)
    {
        if (actorUserId == report.SubmitterUserId) return;

        await auditLogService.LogAsync(
            AuditAction.ExpenseEditedOnBehalf,
            AuditEntityTypes.Report, report.Id,
            $"{whatChanged} on behalf of {await DescribeMemberAsync(report.SubmitterUserId, ct)}.",
            actorUserId,
            relatedEntityId: report.SubmitterUserId,
            relatedEntityType: AuditEntityTypes.User);
    }

    /// <summary>The note as it reads in an audit description; it is optional and often blank.</summary>
    private static string DescribeNote(string? note) =>
        string.IsNullOrWhiteSpace(note) ? "cleared" : $"\"{note}\"";

    public Task<ExpenseMutationResult> UpdateDraftWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin,
        Guid budgetCategoryId, string? note,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var (editableReport, refusal) = await GetEditableReportAsync(reportId, actorUserId, actorIsFinanceAdmin, ct);
            if (refusal is not null) return refusal;
            var report = editableReport!;

            // A draft is not booked to anything yet, so its header resolves through the active year
            // as it always has. A report past submit already belongs to a budget year; re-resolving
            // it through the active year would silently move last year's accounting into this year's
            // books, so it keeps its own year and only that year's categories are accepted.
            string categoryName;
            Guid budgetYearId;
            if (IsPendingApproval(report.Status))
            {
                var snapshot = await budgetService.GetCategoryByIdAsync(budgetCategoryId);
                if (snapshot is null) return ExpenseMutationResult.Failure("Expenses_Validation_CategoryNotFound");
                if (snapshot.BudgetGroup?.BudgetYearId != report.BudgetYearId)
                    return ExpenseMutationResult.Failure("Expenses_Validation_CategoryDifferentBudgetYear");
                categoryName = snapshot.Name;
                budgetYearId = report.BudgetYearId;
            }
            else
            {
                var year = await budgetService.GetActiveYearAsync()
                    ?? throw new InvalidOperationException("No active budget year.");
                var category = year.Groups.SelectMany(g => g.Categories)
                    .FirstOrDefault(c => c.Id == budgetCategoryId)
                    ?? throw new InvalidOperationException("Category not in active year.");
                categoryName = category.Name;
                budgetYearId = year.Id;
            }

            var updated = new ExpenseReport
            {
                Id = reportId,
                BudgetCategoryId = budgetCategoryId,
                BudgetYearId = budgetYearId,
                Note = note,
                UpdatedAt = clock.GetCurrentInstant()
            };
            await repo.UpdateDraftAsync(updated, ct);

            await AuditOnBehalfEditAsync(report, actorUserId,
                $"Updated header (category {categoryName}, subject {DescribeNote(note)})", ct);

            return ExpenseMutationResult.Success;
        }, "Error updating expense report {ReportId}", reportId);

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
        var ok = await repo.AddLineAsync(reportId, line, ct);
        if (!ok) throw new InvalidOperationException("Failed to add line.");

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
            // Travel lines are computed and can no longer be created; this path takes free-text
            // amounts, so it accepts only the receipt-backed types.
            if (lineType is not (ExpenseLineType.Receipt or ExpenseLineType.Invoice))
                return Reject(new("Expenses_Validation_OnlyReceiptAndInvoiceLines", []));
            // Validate the file before creating anything, so a bad upload leaves no half-made line.
            if (file is not null && ValidateAttachmentUpload(file.FileName, file.ContentType, file.Content).Refusal is { } uploadRefusal)
                return Reject(uploadRefusal);

            var (added, report) = await AddLineWithoutAuditAsync(
                reportId, actorUserId, actorIsFinanceAdmin,
                description, amount, lineType, parentLineId, ct);
            if (added.Error is { } addRefusal) return Reject(addRefusal);
            var lineId = added.LineId!.Value;
            if (file is not null)
            {
                ExpenseError? attachmentRefusal;
                try
                {
                    attachmentRefusal = await StoreAttachmentAsync(
                        reportId, actorUserId, actorIsFinanceAdmin, lineId, file.FileName, file.ContentType, file.Content, ct);
                }
                catch
                {
                    // The form retries the whole add, so a line left behind here would duplicate.
                    await repo.RemoveLineAsync(reportId, lineId, CancellationToken.None);
                    throw;
                }
                if (attachmentRefusal is not null)
                {
                    await repo.RemoveLineAsync(reportId, lineId, CancellationToken.None);
                    return Reject(attachmentRefusal);
                }
            }
            await AuditOnBehalfEditAsync(report!, actorUserId,
                $"Added {(parentLineId is null ? "line" : "proof row")} \"{description}\" €{amount}", ct);
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

    private async Task<ExpenseMutationResult> AddComputedLineAsync(
        Guid reportId, Guid actorUserId, string description, decimal amount, ExpenseLineType lineType,
        CancellationToken ct)
    {
        var (added, report) = await AddLineWithoutAuditAsync(
            reportId, actorUserId, false, description, amount, lineType, null, ct);
        if (added.Error is not null) return new(false, added.Error);
        await AuditOnBehalfEditAsync(report!, actorUserId, $"Added line \"{description}\" €{amount}", ct);
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
            // Travel lines carry computed amounts (mileage km×rate, per-diem days×rate) and waive the
            // receipt requirement on that basis. A free-text amount/description edit here would let a
            // submitter claim an arbitrary unreceipted amount on a Mileage/PerDiem line. To change one,
            // remove it and re-add so the amount is always recomputed from its inputs.
            if (existing.LineType is ExpenseLineType.Mileage or ExpenseLineType.PerDiem)
                return ExpenseMutationResult.Failure("Expenses_Validation_TravelLinesComputedCannotEdit");

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

            // One atomic save removes the line, any proof rows under it, and their attachment rows;
            // the files are deleted only after that commit (best-effort — an orphan file is a warning,
            // an orphan row is a bug).
            var removedAttachments = await repo.RemoveLineAsync(reportId, lineId, ct)
                ?? throw new InvalidOperationException("Failed to remove line.");

            // The deletion has committed; its audit and file cleanup must finish even if the request ends.
            await AuditOnBehalfEditAsync(report, actorUserId,
                removed is null
                    ? $"Removed line {lineId}"
                    : $"Removed line \"{removed.Description}\" €{removed.Amount}", CancellationToken.None);

            foreach (var attachment in removedAttachments)
            {
                try
                {
                    await fileStorage.DeleteAsync(
                        AttachmentKey(attachment.Id, attachment.Extension), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Could not delete attachment file {AttachmentId} while removing line {LineId}",
                        attachment.Id, lineId);
                }
            }

            return ExpenseMutationResult.Success;
        }, "Error removing line {LineId} from report {ReportId}", lineId, reportId);

    private const long AttachmentMaxBytes = 20 * 1024 * 1024;
    private const int AttachmentFileNameMaxLength = 255;

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf", "image/jpeg", "image/jpg", "image/png", "image/heic"
    };

    private static (string? Extension, ExpenseError? Refusal) ValidateAttachmentUpload(
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

    private async Task<ExpenseError?> StoreAttachmentAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin,
        Guid lineId, string originalFileName, string contentType,
        Stream content, CancellationToken ct = default)
    {
        var (fileExtension, uploadRefusal) = ValidateAttachmentUpload(originalFileName, contentType, content);
        if (uploadRefusal is not null) return uploadRefusal;
        var extension = fileExtension!;
        var (editableReport, refusal) = await GetEditableReportAsync(reportId, actorUserId, actorIsFinanceAdmin, ct);
        if (refusal is not null) return refusal.Error;
        var report = editableReport!;

        var line = report.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null) return new("Expenses_Validation_LineNotOnReport", []);
        var previousAttachmentId = line.AttachmentId;

        var attachmentId = Guid.NewGuid();
        await fileStorage.SaveAsync(AttachmentKey(attachmentId, extension), content, ct);

        var attachment = new ExpenseAttachment
        {
            Id = attachmentId,
            OriginalFileName = Path.GetFileName(originalFileName),
            Extension = extension,
            ContentType = contentType,
            SizeBytes = content.Length,
            // Who uploaded the file, not whose report it is — an admin filing on a member's behalf
            // is the uploader.
            UploadedByUserId = actorUserId,
            UploadedAt = clock.GetCurrentInstant()
        };
        try
        {
            await repo.AddAttachmentAsync(attachment, ct);
            await repo.SetLineAttachmentAsync(lineId, attachmentId, ct);

            await auditLogService.LogAsync(
                AuditAction.ExpenseAttachmentUploaded,
                AuditEntityTypes.Report, reportId,
                $"Attachment uploaded to line {lineId}.",
                actorUserId,
                relatedEntityId: report.SubmitterUserId,
                relatedEntityType: AuditEntityTypes.User);
        }
        catch
        {
            var metadataRemoved = true;
            try
            {
                // Both repository operations are idempotent. Run them even when the failed write
                // may have committed before throwing, and put any replaced attachment back.
                await repo.SetLineAttachmentAsync(lineId, previousAttachmentId, CancellationToken.None);
                await repo.RemoveAttachmentAsync(attachmentId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                metadataRemoved = false;
                logger.LogError(ex,
                    "Could not roll back attachment metadata {AttachmentId} for line {LineId}",
                    attachmentId, lineId);
            }

            if (metadataRemoved)
            {
                try
                {
                    await fileStorage.DeleteAsync(
                        AttachmentKey(attachmentId, extension), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Could not delete attachment file {AttachmentId} after upload rollback",
                        attachmentId);
                }
            }

            throw;
        }

        return null;
    }

    public Task<ExpenseMutationResult> AttachFileToLineWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin,
        Guid lineId, string originalFileName, string contentType,
        Stream content, CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var refusal = await StoreAttachmentAsync(
                reportId, actorUserId, actorIsFinanceAdmin, lineId, originalFileName, contentType, content, ct);
            return refusal is null ? ExpenseMutationResult.Success : new(false, refusal);
        }, "Error uploading attachment to line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> RemoveAttachmentFromLineAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin,
        Guid lineId, CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var (editableReport, refusal) = await GetEditableReportAsync(reportId, actorUserId, actorIsFinanceAdmin, ct);
            if (refusal is not null) return refusal;
            var report = editableReport!;

            var line = report.Lines.FirstOrDefault(l => l.Id == lineId);
            if (line is null)
                return ExpenseMutationResult.Failure("Expenses_Validation_LineNotOnReport");

            if (line.Attachment is null) return ExpenseMutationResult.Success; // idempotent

            await repo.SetLineAttachmentAsync(lineId, null, ct);
            await repo.RemoveAttachmentAsync(line.Attachment.Id, ct);

            await auditLogService.LogAsync(
                AuditAction.ExpenseAttachmentRemoved,
                AuditEntityTypes.Report, reportId,
                $"Attachment removed from line {lineId}.",
                actorUserId,
                relatedEntityId: report.SubmitterUserId,
                relatedEntityType: AuditEntityTypes.User);

            // Post-commit cleanup: metadata and audit are written, so request cancellation
            // must not strand the file.
            try
            {
                await fileStorage.DeleteAsync(
                    AttachmentKey(line.Attachment.Id, line.Attachment.Extension), CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not delete attachment file {AttachmentId} for line {LineId}",
                    line.Attachment.Id, lineId);
            }

            return ExpenseMutationResult.Success;
        }, "Error removing attachment from line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> SubmitWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin, CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var report = await repo.GetByIdAsync(reportId, ct);
            if (report is null) return ExpenseMutationResult.Failure("Expenses_Validation_CouldNotSubmitReport");
            if (!actorIsFinanceAdmin && report.SubmitterUserId != actorUserId)
                return ExpenseMutationResult.Failure("Expenses_Validation_OnlySubmitterCanSubmit");
            if (report.Status != ExpenseReportStatus.Draft) return ExpenseMutationResult.Failure("Expenses_Validation_CouldNotSubmitReport");

            if (!report.Lines.Any())
                return ExpenseMutationResult.Failure("Expenses_Validation_ReportNeedsLine");

            // Receipt lines (proof rows included) need their receipt; invoice lines need the invoice file.
            if (report.Lines.Any(l => l.LineType is ExpenseLineType.Receipt or ExpenseLineType.Invoice
                                      && l.AttachmentId is null))
                return ExpenseMutationResult.Failure("Expenses_Validation_ReceiptInvoiceNeedAttachment");

            // The payee is whoever the report belongs to — never the person pressing Submit. An admin
            // submitting on a member's behalf must snapshot the *member's* IBAN and legal name, or the
            // money goes to the wrong account.
            var profile = (await userService.GetUserInfoAsync(report.SubmitterUserId, ct))?.Profile;
            if (profile?.Iban is null)
                return ExpenseMutationResult.Failure("Expenses_Validation_SubmitterNeedsIban");

            // Financial records use legal name (not BurnerName). See memory/architecture/burnername-is-the-display-name.md.
            var legalName = $"{profile.FirstName} {profile.LastName}".Trim();
            if (string.IsNullOrWhiteSpace(legalName))
            {
                return ExpenseMutationResult.Failure("Expenses_Validation_SubmitterNeedsFirstAndLastName");
            }
            var payeeIban = profile.Iban;

            var now = clock.GetCurrentInstant();
            var ok = await repo.SubmitAsync(reportId, legalName, payeeIban, now, ct);
            if (!ok) return ExpenseMutationResult.Failure("Expenses_Validation_CouldNotSubmitReport");

            await auditLogService.LogAsync(
                AuditAction.ExpenseSubmit,
                AuditEntityTypes.Report, reportId,
                report.SubmitterUserId == actorUserId
                    ? "Submitted expense report."
                    : $"Submitted expense report on behalf of {await DescribeMemberAsync(report.SubmitterUserId, ct)}.",
                actorUserId,
                relatedEntityId: report.SubmitterUserId,
                relatedEntityType: AuditEntityTypes.User);

            return ExpenseMutationResult.Success;

        }, "Error submitting expense report {ReportId}", reportId);

    public Task<ExpenseMutationResult> WithdrawWithResultAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var report = await repo.GetByIdAsync(reportId, ct);
            if (report is null) return ExpenseMutationResult.Failure("Expenses_Flash_WithdrawFailed");
            if (report.SubmitterUserId != submitterUserId)
                return ExpenseMutationResult.Failure("Expenses_Validation_OnlySubmitterCanWithdraw");

            var now = clock.GetCurrentInstant();
            var ok = await repo.WithdrawAsync(reportId, now, ct);
            if (!ok) return ExpenseMutationResult.Failure("Expenses_Flash_WithdrawFailed");

            await auditLogService.LogAsync(
                AuditAction.ExpenseWithdraw,
                AuditEntityTypes.Report, reportId,
                "Withdrew expense report.",
                submitterUserId);

            return ExpenseMutationResult.Success;

        }, "Error withdrawing expense report {ReportId}", reportId);

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

        // A report past Draft carries a payee IBAN snapshot, and this page is how it gets corrected
        // before approval. Clearing would leave the snapshot and the profile disagreeing about who
        // gets paid, so on those statuses the removal is refused rather than half-applied.
        // A draft has no snapshot yet (submit takes it); an approved or terminal report is already
        // booked, so its snapshot is history. Only the pending window follows the profile.
        var snapshotIsLive = IsPendingApproval(report.Status);
        if (normalized is null && snapshotIsLive)
            return IbanFailure(
                "Expenses_Iban_RequiredForPendingReport",
                isValidationError: true);

        try
        {
            var saved = await userService.SetProfileIbanAsync(submitterUserId, normalized, ct);
            if (!saved)
                return IbanFailure("Expenses_Iban_SaveFailed", isValidationError: false);

            if (snapshotIsLive)
                await RefreshPayeeIbanSnapshotAsync(report, actorUserId, normalized!, ct);

            var isClearing = normalized is null;
            // The entry is about the member, but its entity type is Profile and its actor may be
            // somebody else — without the related id, the member's own GDPR export would miss the
            // row carrying their raw IBAN. Set unconditionally; for a self-set it is a no-op.
            await auditLogService.LogAsync(
                isClearing ? AuditAction.IbanRemove : AuditAction.IbanSet,
                AuditEntityTypes.Profile,
                submitterUserId,
                await DescribeIbanChangeAsync(submitterUserId, actorUserId, normalized, ct),
                actorUserId,
                relatedEntityId: submitterUserId,
                relatedEntityType: AuditEntityTypes.User);

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

    /// <summary>
    /// Submitted but not yet approved — the window where a report is real enough to have a payee
    /// snapshot and a booked budget year, but not yet final. Both the IBAN refresh and the header
    /// edit's year handling turn on it.
    /// </summary>
    private static bool IsPendingApproval(ExpenseReportStatus status) =>
        status is ExpenseReportStatus.Submitted or ExpenseReportStatus.CoordinatorEndorsed;

    private async Task RefreshPayeeIbanSnapshotAsync(
        ExpenseReportDto report, Guid actorUserId, string normalizedIban, CancellationToken ct)
    {
        if (string.Equals(report.PayeeIban, normalizedIban, StringComparison.Ordinal)) return;

        var updated = await repo.UpdatePayeeIbanAsync(
            report.Id, normalizedIban, clock.GetCurrentInstant(), ct);
        if (!updated) return;

        // On the report entity so it lands in that report's on-page history, where the admin
        // correcting it is looking. Unmasked when somebody set it for another member — same ruling
        // as the profile entry above (memory/code/audit-pii-subject-allowed.md).
        var description = actorUserId == report.SubmitterUserId
            ? "Payee IBAN updated"
            : $"Payee IBAN updated for {await DescribeMemberAsync(report.SubmitterUserId, ct)} to {normalizedIban}";

        await auditLogService.LogAsync(
            AuditAction.ExpensePayeeIbanUpdated,
            AuditEntityTypes.Report, report.Id,
            $"{description}.",
            actorUserId,
            relatedEntityId: report.SubmitterUserId,
            relatedEntityType: AuditEntityTypes.User);
    }

    /// <summary>
    /// Audit description for an IBAN change. A member changing their own stays the bare
    /// "IBAN set" / "IBAN removed" it has always been. When somebody else does it, the entry names
    /// the member and carries the account number <b>unmasked</b> — Peter's ruling: audit may hold
    /// PII belonging to the entry's subject, and the only way to trace a wrongly-typed IBAN back to
    /// who typed it is to keep what they typed (memory/code/audit-pii-subject-allowed.md, the one
    /// exception to memory/code/iban-mask-in-logs.md).
    /// </summary>
    private async Task<string> DescribeIbanChangeAsync(
        Guid submitterUserId, Guid actorUserId, string? normalizedIban, CancellationToken ct)
    {
        if (actorUserId == submitterUserId)
            return normalizedIban is null ? "IBAN removed" : "IBAN set";

        var member = await DescribeMemberAsync(submitterUserId, ct);
        return normalizedIban is null
            ? $"IBAN removed for {member}"
            : $"IBAN set for {member} to {normalizedIban}";
    }

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

    /// <summary>Audit-detail suffix for a cap set on this decision; empty when none was set.</summary>
    private static string MaxAmountDetail(decimal? maxAmount) =>
        maxAmount is { } cap
            ? $" Authorized maximum {cap.ToString("0.00", CultureInfo.InvariantCulture)} EUR."
            : "";

    public Task<ExpenseMutationResult> CoordinatorEndorseWithResultAsync(
        Guid reportId, Guid coordinatorUserId, bool actorIsFinanceAdmin, decimal? maxAmount,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var report = await repo.GetByIdAsync(reportId, ct);
            if (report is null) return ExpenseMutationResult.OperatorFailure("Could not endorse the report. It may no longer be in Submitted status.");

            if (!actorIsFinanceAdmin && await GetCoordinatorRefusalAsync(report.BudgetCategoryId, coordinatorUserId, ct) is { } refusal)
                return refusal;

            var now = clock.GetCurrentInstant();
            var ok = await repo.CoordinatorEndorseAsync(reportId, coordinatorUserId, maxAmount, now, ct);
            if (!ok) return ExpenseMutationResult.OperatorFailure("Could not endorse the report. It may no longer be in Submitted status.");

            await auditLogService.LogAsync(
                AuditAction.ExpenseEndorse,
                AuditEntityTypes.Report, reportId,
                (actorIsFinanceAdmin ? "Finance admin" : "Coordinator")
                    + " endorsed expense report." + MaxAmountDetail(maxAmount),
                coordinatorUserId);

            return ExpenseMutationResult.Success;

        }, "Error endorsing expense report {ReportId}", reportId);

    public Task<ExpenseMutationResult> CoordinatorRejectWithResultAsync(
        Guid reportId, Guid coordinatorUserId, bool actorIsFinanceAdmin, string reason,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var report = await repo.GetByIdAsync(reportId, ct);
            if (report is null) return ExpenseMutationResult.OperatorFailure("Could not reject the report. It may no longer be in Submitted status.");

            if (!actorIsFinanceAdmin && await GetCoordinatorRefusalAsync(report.BudgetCategoryId, coordinatorUserId, ct) is { } refusal)
                return refusal;

            var now = clock.GetCurrentInstant();
            var ok = await repo.CoordinatorRejectAsync(reportId, coordinatorUserId, reason, now, ct);
            if (!ok) return ExpenseMutationResult.OperatorFailure("Could not reject the report. It may no longer be in Submitted status.");

            await auditLogService.LogAsync(
                AuditAction.ExpenseCoordinatorReject,
                AuditEntityTypes.Report, reportId,
                $"{(actorIsFinanceAdmin ? "Finance admin" : "Coordinator")} rejected expense report: {reason}",
                coordinatorUserId);

            return ExpenseMutationResult.Success;

        }, "Error coordinator-rejecting expense report {ReportId}", reportId);

    /// <summary>
    /// Tells the submitter their report was approved (peterdrier/Humans#1820). Re-reads the
    /// report after the flip so the amount is the DTO's own <c>Payable</c> — the cap the approver
    /// just set included — rather than a second copy of that formula. Sent after the save: the
    /// outbox row is a promise of money, and a failed approval must not make it.
    /// </summary>
    private async Task SendApprovedEmailAsync(Guid reportId, CancellationToken ct)
    {
        var approved = await GetAsync(reportId, ct);
        if (approved is null) return;

        var submitter = await userService.GetUserInfoAsync(approved.SubmitterUserId, ct);
        var targets = await userEmailService.GetNotificationTargetEmailsAsync([approved.SubmitterUserId], ct);
        if (submitter is null || !targets.TryGetValue(approved.SubmitterUserId, out var recipient)
            || string.IsNullOrWhiteSpace(recipient))
        {
            logger.LogWarning(
                "Skipping expense-approved email for report {ReportId}: submitter {UserId} has no notification email",
                reportId, approved.SubmitterUserId);
            return;
        }

        var language = submitter.PreferredLanguage;
        await emailService.SendAsync(emails.ReportApproved(
            recipient, submitter.BurnerName, reportId, approved.Payable,
            IbanFormatter.Mask(approved.PayeeIban),
            language.IsSupportedCultureCode() ? language : CultureCatalog.DefaultCultureCode), ct);
    }

    public Task<ExpenseMutationResult> ApproveWithResultAsync(
        Guid reportId, Guid actorUserId, Guid? overrideCategoryId, decimal? maxAmount,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var report = await repo.GetByIdAsync(reportId, ct);
            if (report is null) return ExpenseMutationResult.OperatorFailure("Could not approve the report. It may not be in an approvable status.");

            var outboxEventId = Guid.NewGuid();
            var now = clock.GetCurrentInstant();
            var ok = await repo.ApproveAsync(
                reportId, actorUserId, overrideCategoryId, maxAmount, now, outboxEventId, ct);
            if (!ok) return ExpenseMutationResult.OperatorFailure("Could not approve the report. It may not be in an approvable status.");

            await auditLogService.LogAsync(
                AuditAction.ExpenseApprove,
                AuditEntityTypes.Report, reportId,
                "Finance approved expense report." + MaxAmountDetail(maxAmount),
                actorUserId);

            if (overrideCategoryId.HasValue && overrideCategoryId.Value != report.BudgetCategoryId)
            {
                await auditLogService.LogAsync(
                    AuditAction.ExpenseCategoryOverride,
                    AuditEntityTypes.Report, reportId,
                    $"Category overridden during approval to {overrideCategoryId.Value}.",
                    actorUserId);
            }

            // The approval is committed and its Holded push queued; a failed notice must not
            // report it as a failed approval.
            try
            {
                await SendApprovedEmailAsync(reportId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Expense report {ReportId} approved but the approval email failed", reportId);
            }

            return ExpenseMutationResult.Success;

        }, "Error approving expense report {ReportId}", reportId);

    public Task<ExpenseMutationResult> FinanceRejectWithResultAsync(
        Guid reportId, Guid actorUserId, string reason,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var report = await repo.GetByIdAsync(reportId, ct);
            if (report is null) return ExpenseMutationResult.OperatorFailure("Could not reject the report. It may not be in a rejectable status.");

            var now = clock.GetCurrentInstant();
            var ok = await repo.FinanceRejectAsync(reportId, actorUserId, reason, now, ct);
            if (!ok) return ExpenseMutationResult.OperatorFailure("Could not reject the report. It may not be in a rejectable status.");

            await auditLogService.LogAsync(
                AuditAction.ExpenseReject,
                AuditEntityTypes.Report, reportId,
                $"Finance rejected expense report: {reason}",
                actorUserId);

            return ExpenseMutationResult.Success;

        }, "Error finance-rejecting expense report {ReportId}", reportId);

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
                AuditAction.ExpenseHoldedRequeued,
                AuditEntityTypes.Report, reportId,
                "Finance re-queued the Holded push.",
                actorUserId);

            return ExpenseMutationResult.Success;

        }, "Error re-queuing Holded push for expense report {ReportId}", reportId);

    internal async Task<bool> CategoryRequiresCoordinatorEndorsementAsync(
        Guid categoryId, CancellationToken ct = default)
    {
        var category = await budgetService.GetCategoryByIdAsync(categoryId);
        if (category is null || category.TeamId is null)
            return false;

        var team = await teamService.GetTeamAsync(category.TeamId.Value, ct);
        if (team is null)
            return false;

        return team.Members.Any(m => m.Role == TeamMemberRole.Coordinator);
    }

    Task IExpenseReportBackgroundProcessor.DrainHoldedOutboxAsync(
        int batchSize, CancellationToken ct)
        => DrainHoldedOutboxAsync(batchSize, ct);

    internal async Task DrainHoldedOutboxAsync(int batchSize, CancellationToken ct = default)
    {
        // No Holded API key (PR-preview / local dev envs) → don't drain. A 401 here is a permanent
        // error that would write off every queued event. Debug-level: this job runs every minute.
        // The same flag is what makes /Expenses/{id} report NotConfigured instead of Queued.
        if (!holdedClient.IsConfigured)
        {
            logger.LogDebug(
                "HOLDED_API_KEY_V2 not configured — skipping Holded expense outbox drain.");
            return;
        }

        var events = await repo
            .GetUnprocessedOutboxAsync(clock.GetCurrentInstant(), batchSize, ct);

        if (events.Count == 0)
        {
            return;
        }

        foreach (var outboxEvent in events)
        {
            try
            {
                var report = await repo
                    .GetByIdAsync(outboxEvent.ExpenseReportId, ct);

                if (report is null)
                {
                    logger.LogWarning(
                        "Outbox event {OutboxEventId} references missing report {ReportId} — marking permanently failed",
                        outboxEvent.Id, outboxEvent.ExpenseReportId);
                    await WriteOffOutboxEventAsync(
                        outboxEvent, "Report not found", ct);
                    continue;
                }

                var submitterName = string.IsNullOrWhiteSpace(report.PayeeName)
                    ? "Unknown"
                    : report.PayeeName;

                var now = clock.GetCurrentInstant();

                switch (outboxEvent.EventType)
                {
                    case HoldedExpenseOutboxEventType.CreateIncomingDoc:
                        await holdedPublisher.PublishAsync(
                            outboxEvent.Id, report, submitterName, now, ct);
                        break;

                    case HoldedExpenseOutboxEventType.UpdateIncomingDocTag:
                        // v2 has no tag/doc-update endpoint (PUT /purchases/{id} is a full-replacement
                        // update with no tags field, and no separate tag-assignment endpoint exists).
                        // Recategorize-after-push is now done by reclassifying the line inside Holded
                        // directly; the ledger mirror + reconciliation pull the correction back. The
                        // enum member stays so any queued rows from before this change still drain
                        // instead of poisoning the outbox.
                        logger.LogInformation(
                            "Skipping UpdateIncomingDocTag outbox event {OutboxEventId} for report " +
                            "{ReportId} — Holded v2 has no tag/doc-update endpoint; recategorize is " +
                            "now done by reclassifying the line directly in Holded.",
                            outboxEvent.Id, report.Id);
                        await repo.MarkOutboxProcessedAsync(outboxEvent.Id, now, ct);
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Unknown outbox event type '{outboxEvent.EventType}'.");
                }
            }
            catch (HoldedTransientException ex)
            {
                var attempts = outboxEvent.RetryCount + 1;
                if (attempts >= MaxOutboxRetries)
                {
                    logger.LogError(
                        ex,
                        "Holded outbox event {OutboxEventId} exhausted its {MaxRetries} attempts — writing it off",
                        outboxEvent.Id, MaxOutboxRetries);
                    await WriteOffOutboxEventAsync(
                        outboxEvent,
                        $"Gave up after {attempts} attempts. Last error: {ex.Message}",
                        ct);
                }
                else
                {
                    // Same curve as the Email outbox: 2, 4, 8 … minutes, so a Holded outage longer
                    // than a few minutes is survived instead of being re-hit every 60 seconds.
                    var nextRetryAt = clock.GetCurrentInstant()
                        + Duration.FromMinutes((long)Math.Pow(2, attempts));
                    logger.LogWarning(
                        ex,
                        "Transient error processing Holded outbox event {OutboxEventId} — attempt {Attempt}/{MaxRetries}, retrying at {NextRetryAt}",
                        outboxEvent.Id, attempts, MaxOutboxRetries, nextRetryAt);
                    await repo.IncrementOutboxRetryAsync(
                        outboxEvent.Id, BoundOutboxError(ex.Message), nextRetryAt, ct);
                }
            }
            catch (HoldedPermanentException ex)
            {
                logger.LogError(
                    ex,
                    "Permanent error processing Holded outbox event {OutboxEventId} — HTTP {StatusCode}",
                    outboxEvent.Id, ex.StatusCode);
                await WriteOffOutboxEventAsync(outboxEvent, ex.Message, ct);
            }
        }
    }

    /// <summary>
    /// Writes an outbox event off and records why in the audit log. The outbox columns alone are
    /// not readable outside the database and do not survive row cleanup; the audit entry is what
    /// keeps "this push failed, here is the error" on the report's history
    /// (nobodies-collective/Humans#1045).
    /// </summary>
    private async Task WriteOffOutboxEventAsync(
        HoldedExpenseOutboxEvent outboxEvent, string error, CancellationToken ct)
    {
        error = BoundOutboxError(error);
        await repo.MarkOutboxFailedPermanentlyAsync(
            outboxEvent.Id, error, clock.GetCurrentInstant(), ct);

        await auditLogService.LogAsync(
            AuditAction.ExpenseHoldedFailed,
            AuditEntityTypes.Report, outboxEvent.ExpenseReportId,
            $"Holded push failed permanently: {error}",
            OutboxJobName);
    }

    // Fits the outbox's varchar(2000) and the prefixed audit description; logs retain the exception.
    private static string BoundOutboxError(string error)
    {
        const int maxLength = 2000;
        if (error.Length <= maxLength) return error;
        var length = maxLength;
        if (char.IsHighSurrogate(error[length - 1]) && char.IsLowSurrogate(error[length]))
            length--;
        return error[..length];
    }

    /// <summary>
    /// The service-side half of the edit gate (the resource-based handler is the first half). For
    /// the member whose report it is, editing is their own Draft and nothing else. A finance admin
    /// edits on their behalf, so ownership is waived and the window covers the three statuses a
    /// report can still be corrected in; Approved and Withdrawn are closed to everyone.
    /// </summary>
    private async Task<(ExpenseReportDto? Report, ExpenseMutationResult? Refusal)> GetEditableReportAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin, CancellationToken ct)
    {
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null)
            return (null, ExpenseMutationResult.Failure("Expenses_Iban_ReportNotFound"));
        if (!actorIsFinanceAdmin && report.SubmitterUserId != actorUserId)
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_OnlySubmitterCanEdit"));

        var editable = actorIsFinanceAdmin
            ? report.Status is ExpenseReportStatus.Draft or ExpenseReportStatus.Submitted or ExpenseReportStatus.CoordinatorEndorsed
            : report.Status is ExpenseReportStatus.Draft;
        return editable
            ? (report, null)
            : (null, ExpenseMutationResult.Failure("Expenses_Validation_ReportCannotBeEditedInStatus", report.Status));
    }

    private async Task<ExpenseMutationResult?> GetCoordinatorRefusalAsync(
        Guid categoryId, Guid actorUserId, CancellationToken ct)
    {
        var category = await budgetService.GetCategoryByIdAsync(categoryId);
        if (category is null)
            throw new InvalidOperationException("Budget category not found.");
        if (!category.TeamId.HasValue)
            return ExpenseMutationResult.OperatorFailure("Category has no owning team; coordinator endorsement is not valid.");
        var isCoordinator = await teamService.IsUserCoordinatorOfTeamAsync(
            category.TeamId.Value, actorUserId, ct);
        if (!isCoordinator)
            return ExpenseMutationResult.OperatorFailure("Actor is not a coordinator of the category's team.");
        return null;
    }

    /// <summary>User's reports (lines+attachment metadata), masked IBAN, audit. Includes accounts merged into this one.</summary>
    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(
        Guid userId, CancellationToken ct)
    {
        // Every id the human has held, from the resolved record: asked with an archived
        // id, the survivor's own reports are theirs too.
        var user = await userService.GetUserInfoAsync(userId, ct);
        var allIds = user?.AllUserIds ?? [userId];

        var allReports = new List<ExpenseReportDto>();
        foreach (var id in allIds)
        {
            var reports = await repo.GetForSubmitterAsync(id, ct);
            allReports.AddRange(reports);
        }

        var profile = user?.Profile;
        var maskedIban = string.IsNullOrEmpty(profile?.Iban)
            ? null
            : IbanFormatter.Mask(profile.Iban);

        var expenseActions = new List<AuditAction>
        {
            AuditAction.ExpenseCreatedOnBehalf,
            AuditAction.ExpenseEditedOnBehalf,
            AuditAction.ExpensePayeeIbanUpdated,
            AuditAction.ExpenseSubmit,
            AuditAction.ExpenseEndorse,
            AuditAction.ExpenseCoordinatorReject,
            AuditAction.ExpenseApprove,
            AuditAction.ExpenseReject,
            AuditAction.ExpenseWithdraw,
            AuditAction.ExpenseCategoryOverride,
            AuditAction.ExpenseSepaSent,
            AuditAction.ExpenseSepaReopened,
            AuditAction.ExpensePaid,
            AuditAction.ExpenseAttachmentUploaded,
            AuditAction.ExpenseAttachmentRemoved,
            AuditAction.IbanSet,
            AuditAction.IbanRemove,
            AuditAction.IbanReveal,
        };

        var auditEntries = await auditLogService.GetFilteredEntriesAsync(
            userId: userId,
            actions: expenseActions,
            limit: 10_000,
            ct: ct);

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
                SubmittedAt = r.SubmittedAt?.ToIso8601(),
                ApprovedAt = r.ApprovedAt?.ToIso8601(),
                CreatedAt = r.CreatedAt.ToIso8601(),
                Lines = r.Lines.Select(l => new
                {
                    l.Id,
                    l.Description,
                    l.Amount,
                    l.LineType,
                    l.ParentLineId,
                    l.SortOrder,
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
                e.Action,
                e.EntityType,
                e.EntityId,
                e.Description,
                OccurredAt = e.OccurredAt.ToIso8601()
            }).ToList();

        return
        [
            new UserDataSlice(ExpenseReports,
                shapedReports.Count > 0 ? shapedReports : null),
            new UserDataSlice(ExpenseAuditLog,
                shapedAudit.Count > 0
                    ? new { MaskedIban = maskedIban, Entries = shapedAudit }
                    : (object?)null),
        ];
    }

    // ─── IUserDataContributor (GDPR erasure) ───

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
