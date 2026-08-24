using Humans.Base.Attributes;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Budget.Contracts;
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
/// Application-layer orchestrator for Expense Reports. Coordinates
/// <see cref="IExpenseRepository"/>, audit logging, IBAN snapshots, and
/// cross-section reads via interfaces — never imports EF Core directly.
/// </summary>
[CrossSectionWrite("Writes the reimbursement IBAN onto the claimant profile.")]
internal sealed class ExpenseReportService(
    IExpenseRepository repo,
    IFileStorage fileStorage,
    IBudgetServiceRead budgetService,
    ITeamService teamService,
    IUserService userService,
    IAuditLogService auditLogService,
    IHoldedClient holdedClient,
    IHoldedFinanceService holdedFinance,
    IClock clock,
    ILogger<ExpenseReportService> logger,
    IOptions<TravelReimbursementConfig> travelConfig) : IExpenseReportService,
        IExpenseReportBackgroundProcessor, IUserDataContributor
{
    private readonly TravelReimbursementConfig _travel = travelConfig.Value;

    /// <summary>
    /// Attempts a Holded push gets before it is written off. With the 2^n-minute backoff below,
    /// ten attempts span roughly 17 hours — long enough to ride out a Holded outage, short enough
    /// that a genuinely broken push surfaces on /Expenses/Review the same day.
    /// </summary>
    private const int MaxOutboxRetries = 10;

    /// <summary>Audit actor for pushes, which run unattended. Matches the Hangfire job's type name.</summary>
    private const string OutboxJobName = "HoldedExpenseOutboxJob";

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
            // A report with a HoldedDocId is booked as a payable in Holded (the purchase doc is created
            // at outbox-drain time), so it contributes to the creditor balance from Approved onward.
            // Approved is the report's terminal state — paid/unpaid is read from the account ledger, never the report.
            memberRegisteredTotal = memberReports
                .Where(r => r.HoldedDocId is not null
                         && r.Status is ExpenseReportStatus.Approved)
                .Sum(r => r.Payable);

            owed = status?.OwedToMember ?? 0m;
            totalPaid = status?.TotalPaid ?? 0m;
            // Settled iff the derived creditor balance (Σdebit − Σcredit) is non-negative. A null status
            // means no cached ledger lines for the account — unknown, not settled.
            paid = status is { } s && s.Balance >= 0m;
            paidOn = status?.LastPaymentDate;
        }

        return new ExpenseHoldedTimeline
        {
            RegisteredInHolded = report.HoldedDocId is not null,
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
        // memory beats a per-audience query at ~500 users, and keeps the ordering the repo
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
        Guid submitterUserId, Guid budgetCategoryId, string? note,
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
        return report.Id;
    }

    internal async Task UpdateDraftAsync(
        Guid reportId, Guid submitterUserId,
        Guid budgetCategoryId, string? note,
        CancellationToken ct = default)
    {
        var existing = await repo.GetByIdAsync(reportId, ct)
            ?? throw new ExpenseValidationException("Report not found.");
        if (existing.SubmitterUserId != submitterUserId)
            throw new UnauthorizedAccessException("Only the submitter can update a draft.");
        if (existing.Status != ExpenseReportStatus.Draft)
            throw new ExpenseValidationException("Only Draft reports can be updated.");

        var year = await budgetService.GetActiveYearAsync()
            ?? throw new InvalidOperationException("No active budget year.");
        var category = year.Groups.SelectMany(g => g.Categories)
            .FirstOrDefault(c => c.Id == budgetCategoryId)
            ?? throw new InvalidOperationException("Category not in active year.");

        var updated = new ExpenseReport
        {
            Id = reportId,
            BudgetCategoryId = category.Id,
            BudgetYearId = year.Id,
            Note = note,
            UpdatedAt = clock.GetCurrentInstant()
        };
        await repo.UpdateDraftAsync(updated, ct);
    }

    public Task<ExpenseMutationResult> UpdateDraftWithResultAsync(
        Guid reportId, Guid submitterUserId,
        Guid budgetCategoryId, string? note,
        CancellationToken ct = default) =>
        RunMutationAsync(async () =>
        {
            await UpdateDraftAsync(reportId, submitterUserId, budgetCategoryId, note, ct);
            return ExpenseMutationResult.Success;
        }, "Error updating expense report {ReportId}", null, reportId);

    internal async Task<Guid> AddLineAsync(
        Guid reportId, Guid submitterUserId,
        string description, decimal amount,
        ExpenseLineType lineType = ExpenseLineType.Receipt,
        Guid? parentLineId = null,
        CancellationToken ct = default)
    {
        var report = await RequireEditableReportAsync(reportId, submitterUserId, ct);

        if (parentLineId is { } parentId)
        {
            // A proof row is a Receipt backing an Invoice line on the same report. One level only.
            if (lineType != ExpenseLineType.Receipt)
                throw new ExpenseValidationException("Proof rows must be receipt lines.");
            var parent = report.Lines.FirstOrDefault(l => l.Id == parentId)
                ?? throw new ExpenseValidationException("Parent line not found on this report.");
            if (parent.LineType != ExpenseLineType.Invoice)
                throw new ExpenseValidationException("Proof rows can only be added to an invoice line.");
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
        return line.Id;
    }

    public async Task<ExpenseAddLineResult> AddLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        string description, decimal amount,
        ExpenseLineType lineType = ExpenseLineType.Receipt,
        Guid? parentLineId = null,
        ExpenseFileUpload? file = null,
        CancellationToken ct = default)
    {
        try
        {
            // Travel lines are computed and can no longer be created; this path takes free-text
            // amounts, so it accepts only the receipt-backed types.
            if (lineType is not (ExpenseLineType.Receipt or ExpenseLineType.Invoice))
                throw new ExpenseValidationException("Only receipt and invoice lines can be added.");
            // Validate the file before creating anything, so a bad upload leaves no half-made line.
            if (file is not null)
                ValidateAttachmentUpload(file.FileName, file.ContentType, file.Content);

            var lineId = await AddLineAsync(
                reportId, submitterUserId, description, amount, lineType, parentLineId, ct);
            if (file is not null)
            {
                try
                {
                    await AttachFileToLineAsync(
                        reportId, submitterUserId, lineId, file.FileName, file.ContentType, file.Content, ct);
                }
                catch
                {
                    // The form retries the whole add, so a line left behind here would duplicate.
                    await repo.RemoveLineAsync(reportId, lineId, ct);
                    throw;
                }
            }
            return new ExpenseAddLineResult(true, null, lineId);
        }
        catch (ExpenseValidationException ex)
        {
            logger.LogWarning("Error adding line to report {ReportId}: {Reason}", reportId, ex.Message);
            return new ExpenseAddLineResult(false, ex.Message, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error adding line to report {ReportId}", reportId);
            return new ExpenseAddLineResult(false, ex.Message, null);
        }
    }

    public Task<ExpenseMutationResult> AddMileageLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        string origin, string destination, decimal km,
        CancellationToken ct = default) =>
        RunMutationAsync(async () =>
        {
            var rate = _travel.MileageRatePerKm;
            var amount = Math.Round(km * rate, 2, MidpointRounding.AwayFromZero);
            var description =
                $"{origin.Trim()} to {destination.Trim()}, " +
                $"{km.ToString("0.#", CultureInfo.InvariantCulture)} km @ " +
                $"€{rate.ToString("0.00", CultureInfo.InvariantCulture)} = " +
                $"€{amount.ToString("0.00", CultureInfo.InvariantCulture)}";
            await AddLineAsync(reportId, submitterUserId, description, amount, ExpenseLineType.Mileage, ct: ct);
            return ExpenseMutationResult.Success;
        }, "Error adding mileage line to report {ReportId}", null, reportId);

    public Task<ExpenseMutationResult> AddPerDiemLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        PerDiemKind kind, int days, string? note,
        CancellationToken ct = default) =>
        RunMutationAsync(async () =>
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
            await AddLineAsync(reportId, submitterUserId, description, amount, ExpenseLineType.PerDiem, ct: ct);
            return ExpenseMutationResult.Success;
        }, "Error adding per-diem line to report {ReportId}", null, reportId);

    internal async Task UpdateLineAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, string description, decimal amount,
        CancellationToken ct = default)
    {
        var report = await RequireEditableReportAsync(reportId, submitterUserId, ct);

        var existing = report.Lines.FirstOrDefault(l => l.Id == lineId)
            ?? throw new UnauthorizedAccessException("Line does not belong to the specified report.");
        // Travel lines carry computed amounts (mileage km×rate, per-diem days×rate) and waive the
        // receipt requirement on that basis. A free-text amount/description edit here would let a
        // submitter claim an arbitrary unreceipted amount on a Mileage/PerDiem line. To change one,
        // remove it and re-add so the amount is always recomputed from its inputs.
        if (existing.LineType is ExpenseLineType.Mileage or ExpenseLineType.PerDiem)
            throw new ExpenseValidationException(
                "Travel lines are computed from their inputs and cannot be edited. Remove the line and add it again to change it.");

        var line = new ExpenseLine
        {
            Id = lineId,
            ExpenseReportId = reportId,
            Description = description,
            Amount = amount
        };
        var ok = await repo.UpdateLineAsync(reportId, line, ct);
        if (!ok) throw new InvalidOperationException("Failed to update line.");
    }

    public Task<ExpenseMutationResult> UpdateLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, string description, decimal amount,
        CancellationToken ct = default) =>
        RunMutationAsync(async () =>
        {
            await UpdateLineAsync(reportId, submitterUserId, lineId, description, amount, ct);
            return ExpenseMutationResult.Success;
        }, "Error updating line {LineId} on report {ReportId}", null, lineId, reportId);

    internal async Task RemoveLineAsync(
        Guid reportId, Guid submitterUserId, Guid lineId,
        CancellationToken ct = default)
    {
        await RequireEditableReportAsync(reportId, submitterUserId, ct);

        // One atomic save removes the line, any proof rows under it, and their attachment rows;
        // the files are deleted only after that commit (best-effort — an orphan file is a warning,
        // an orphan row is a bug).
        var removedAttachments = await repo.RemoveLineAsync(reportId, lineId, ct)
            ?? throw new InvalidOperationException("Failed to remove line.");

        foreach (var attachment in removedAttachments)
        {
            try
            {
                await fileStorage.DeleteAsync(
                    AttachmentKey(attachment.Id, attachment.Extension), ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not delete attachment file {AttachmentId} while removing line {LineId}",
                    attachment.Id, lineId);
            }
        }
    }

    public Task<ExpenseMutationResult> RemoveLineWithResultAsync(
        Guid reportId, Guid submitterUserId, Guid lineId,
        CancellationToken ct = default) =>
        RunMutationAsync(async () =>
        {
            await RemoveLineAsync(reportId, submitterUserId, lineId, ct);
            return ExpenseMutationResult.Success;
        }, "Error removing line {LineId} from report {ReportId}", null, lineId, reportId);

    private const long AttachmentMaxBytes = 20 * 1024 * 1024;

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf", "image/jpeg", "image/jpg", "image/png", "image/heic"
    };

    private static void ValidateAttachmentUpload(
        string originalFileName, string contentType, Stream content)
    {
        if (content is null || content.Length == 0)
            throw new ExpenseValidationException("Please select a file.");
        if (content.Length > AttachmentMaxBytes)
            throw new ExpenseValidationException($"File too large. Maximum size is {AttachmentMaxBytes / (1024 * 1024)} MB.");

        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        if (!AllowedContentTypes.Contains(contentType) || !AllowedExtensions.Contains(extension))
            throw new ExpenseValidationException("Unsupported file type. Upload PDF, JPEG, PNG, or HEIC.");
    }

    internal async Task<Guid> AttachFileToLineAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, string originalFileName, string contentType,
        Stream content, CancellationToken ct = default)
    {
        ValidateAttachmentUpload(originalFileName, contentType, content);
        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();

        var report = await RequireEditableReportAsync(reportId, submitterUserId, ct);

        if (!report.Lines.Any(l => l.Id == lineId))
            throw new UnauthorizedAccessException("Line does not belong to the specified report.");

        var attachmentId = Guid.NewGuid();
        await fileStorage.SaveAsync(AttachmentKey(attachmentId, extension), content, ct);

        var attachment = new ExpenseAttachment
        {
            Id = attachmentId,
            OriginalFileName = Path.GetFileName(originalFileName),
            Extension = extension,
            ContentType = contentType,
            SizeBytes = content.Length,
            UploadedByUserId = submitterUserId,
            UploadedAt = clock.GetCurrentInstant()
        };
        await repo.AddAttachmentAsync(attachment, ct);
        await repo.SetLineAttachmentAsync(lineId, attachmentId, ct);

        await auditLogService.LogAsync(
            AuditAction.ExpenseAttachmentUploaded,
            AuditEntityTypes.Report, reportId,
            $"Attachment uploaded to line {lineId}.",
            submitterUserId);

        return attachmentId;
    }

    public Task<ExpenseMutationResult> AttachFileToLineWithResultAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, string originalFileName, string contentType,
        Stream content, CancellationToken ct = default) =>
        RunMutationAsync(async () =>
        {
            await AttachFileToLineAsync(reportId, submitterUserId, lineId, originalFileName, contentType, content, ct);
            return ExpenseMutationResult.Success;
        }, "Error uploading attachment to line {LineId} on report {ReportId}", null, lineId, reportId);

    public async Task RemoveAttachmentFromLineAsync(
        Guid reportId, Guid submitterUserId,
        Guid lineId, CancellationToken ct = default)
    {
        var report = await RequireEditableReportAsync(reportId, submitterUserId, ct);

        var line = report.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null)
            throw new UnauthorizedAccessException("Line does not belong to the specified report.");

        if (line.Attachment is null) return; // idempotent

        await repo.SetLineAttachmentAsync(lineId, null, ct);
        await repo.RemoveAttachmentAsync(line.Attachment.Id, ct);

        try
        {
            await fileStorage.DeleteAsync(
                AttachmentKey(line.Attachment.Id, line.Attachment.Extension), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not delete attachment file {AttachmentId} for line {LineId}",
                line.Attachment.Id, lineId);
        }

        await auditLogService.LogAsync(
            AuditAction.ExpenseAttachmentRemoved,
            AuditEntityTypes.Report, reportId,
            $"Attachment removed from line {lineId}.",
            submitterUserId);
    }

    internal async Task<bool> SubmitAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default)
    {
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;
        if (report.SubmitterUserId != submitterUserId)
            throw new UnauthorizedAccessException("Only the submitter can submit.");
        if (report.Status != ExpenseReportStatus.Draft) return false;

        if (!report.Lines.Any())
            throw new ExpenseValidationException("Report must have at least one line.");

        // Receipt lines (proof rows included) need their receipt; invoice lines need the invoice file.
        if (report.Lines.Any(l => l.LineType is ExpenseLineType.Receipt or ExpenseLineType.Invoice
                                  && l.AttachmentId is null))
            throw new ExpenseValidationException("Receipt and invoice lines must have an attachment before submitting.");

        var profile = (await userService.GetUserInfoAsync(submitterUserId, ct))?.Profile;
        if (profile?.Iban is null)
            throw new ExpenseValidationException("Submitter must have an IBAN set on their profile.");

        // Financial records use legal name (not BurnerName). See memory/architecture/burnername-is-the-display-name.md.
        var legalName = $"{profile.FirstName} {profile.LastName}".Trim();
        if (string.IsNullOrWhiteSpace(legalName))
        {
            throw new ExpenseValidationException("Submitter must have first and last name set on their profile.");
        }
        var payeeIban = profile.Iban;

        var now = clock.GetCurrentInstant();
        var ok = await repo.SubmitAsync(reportId, legalName, payeeIban, now, ct);
        if (!ok) return false;

        await auditLogService.LogAsync(
            AuditAction.ExpenseSubmit,
            AuditEntityTypes.Report, reportId,
            "Submitted expense report.",
            submitterUserId);

        return true;
    }

    public Task<ExpenseMutationResult> SubmitWithResultAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default) =>
        RunMutationAsync(async () =>
        {
            var submitted = await SubmitAsync(reportId, submitterUserId, ct);
            return submitted
                ? ExpenseMutationResult.Success
                : ExpenseMutationResult.Failure("Could not submit the report. Receipt lines need an attachment and your payment IBAN must be set.");
        }, "Error submitting expense report {ReportId}", "Submission failed", reportId);

    internal async Task<bool> WithdrawAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default)
    {
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;
        if (report.SubmitterUserId != submitterUserId)
            throw new UnauthorizedAccessException("Only the submitter can withdraw.");

        var now = clock.GetCurrentInstant();
        var ok = await repo.WithdrawAsync(reportId, now, ct);
        if (!ok) return false;

        await auditLogService.LogAsync(
            AuditAction.ExpenseWithdraw,
            AuditEntityTypes.Report, reportId,
            "Withdrew expense report.",
            submitterUserId);

        return true;
    }
    public Task<ExpenseMutationResult> WithdrawWithResultAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default) =>
        RunMutationAsync(async () =>
        {
            var withdrawn = await WithdrawAsync(reportId, submitterUserId, ct);
            return withdrawn
                ? ExpenseMutationResult.Success
                : ExpenseMutationResult.Failure("Could not withdraw this report.");
        }, "Error withdrawing expense report {ReportId}", "Withdrawal failed", reportId);

    public async Task<ExpenseIbanSaveResult> SaveSubmitterIbanWithResultAsync(
        Guid submitterUserId, string? iban, CancellationToken ct = default)
    {
        var ibanValue = string.IsNullOrWhiteSpace(iban) ? null : iban.Trim();

        if (ibanValue is not null && !IbanValidator.IsValid(ibanValue))
            return IbanFailure("Invalid IBAN format.", isValidationError: true);

        var normalized = ibanValue is null ? null : IbanValidator.Normalize(ibanValue);
        try
        {
            var saved = await userService.SetProfileIbanAsync(submitterUserId, normalized, ct);
            if (!saved)
                return IbanFailure("Failed to save IBAN.", isValidationError: false);

            var isClearing = normalized is null;
            await auditLogService.LogAsync(
                isClearing ? AuditAction.IbanRemove : AuditAction.IbanSet,
                AuditEntityTypes.Profile,
                submitterUserId,
                isClearing ? "IBAN removed" : "IBAN set",
                submitterUserId);

            logger.LogInformation(
                "IBAN {Action} for user {UserId}",
                isClearing ? "removed" : "set",
                submitterUserId);

            return new ExpenseIbanSaveResult(
                Succeeded: true,
                IsValidationError: false,
                Message: normalized is null ? "IBAN removed." : "IBAN saved.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error setting IBAN for user {UserId}", submitterUserId);
            return IbanFailure("Failed to save IBAN.", isValidationError: false);
        }
    }

    private static ExpenseIbanSaveResult IbanFailure(string message, bool isValidationError) =>
        new(Succeeded: false, IsValidationError: isValidationError, Message: message);

    /// <summary>
    /// Signals an ordinary, user-driven validation rejection (missing attachment, missing IBAN,
    /// empty report, etc.) as distinct from a genuine dependency/system fault. Only the guard
    /// clauses for expected, user-correctable conditions should throw this — never wrap an
    /// arbitrary <see cref="InvalidOperationException"/> surfaced by EF Core, IUserService,
    /// IFileStorage, or another dependency, since those indicate a real fault that must stay
    /// visible at Error with its stack trace.
    /// </summary>
    private sealed class ExpenseValidationException : InvalidOperationException
    {
        public ExpenseValidationException() { }
        public ExpenseValidationException(string message) : base(message) { }
        public ExpenseValidationException(string message, Exception inner) : base(message, inner) { }
    }

    private async Task<ExpenseMutationResult> RunMutationAsync(
        Func<Task<ExpenseMutationResult>> mutation,
        string logMessage,
        string? exceptionPrefix,
        params object?[] logArgs)
    {
        try
        {
            return await mutation();
        }
        catch (ExpenseValidationException ex)
        {
            // Expected, user-driven rejection — log at Warning with no stack trace so it doesn't
            // pollute the Error log, but keep the caller's structured identifiers (report/line IDs)
            // plus the reason, so it's still traceable to the affected mutation.
            logger.LogWarning($"{logMessage}: {{Reason}}", [.. logArgs, ex.Message]);
            return ExpenseMutationResult.Failure(exceptionPrefix is null
                ? ex.Message
                : $"{exceptionPrefix}: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, logMessage, logArgs);
            return ExpenseMutationResult.Failure(exceptionPrefix is null
                ? ex.Message
                : $"{exceptionPrefix}: {ex.Message}");
        }
    }

    internal async Task<bool> CoordinatorEndorseAsync(
        Guid reportId, Guid coordinatorUserId, decimal? maxAmount,
        CancellationToken ct = default)
    {
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;

        await RequireCoordinatorForCategoryAsync(report.BudgetCategoryId, coordinatorUserId, ct);

        var now = clock.GetCurrentInstant();
        var ok = await repo.CoordinatorEndorseAsync(reportId, coordinatorUserId, maxAmount, now, ct);
        if (!ok) return false;

        await auditLogService.LogAsync(
            AuditAction.ExpenseEndorse,
            AuditEntityTypes.Report, reportId,
            "Coordinator endorsed expense report." + MaxAmountDetail(maxAmount),
            coordinatorUserId);

        return true;
    }

    /// <summary>Audit-detail suffix for a cap set on this decision; empty when none was set.</summary>
    private static string MaxAmountDetail(decimal? maxAmount) =>
        maxAmount is { } cap
            ? $" Authorized maximum {cap.ToString("0.00", CultureInfo.InvariantCulture)} EUR."
            : "";

    public Task<ExpenseMutationResult> CoordinatorEndorseWithResultAsync(
        Guid reportId, Guid coordinatorUserId, decimal? maxAmount,
        CancellationToken ct = default) =>
        RunMutationAsync(async () =>
        {
            var endorsed = await CoordinatorEndorseAsync(reportId, coordinatorUserId, maxAmount, ct);
            return endorsed
                ? ExpenseMutationResult.Success
                : ExpenseMutationResult.Failure("Could not endorse the report. It may no longer be in Submitted status.");
        }, "Error endorsing expense report {ReportId}", "Endorsement failed", reportId);

    internal async Task<bool> CoordinatorRejectAsync(
        Guid reportId, Guid coordinatorUserId, string reason,
        CancellationToken ct = default)
    {
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;

        await RequireCoordinatorForCategoryAsync(report.BudgetCategoryId, coordinatorUserId, ct);

        var now = clock.GetCurrentInstant();
        var ok = await repo.CoordinatorRejectAsync(reportId, coordinatorUserId, reason, now, ct);
        if (!ok) return false;

        await auditLogService.LogAsync(
            AuditAction.ExpenseCoordinatorReject,
            AuditEntityTypes.Report, reportId,
            $"Coordinator rejected expense report: {reason}",
            coordinatorUserId);

        return true;
    }

    public Task<ExpenseMutationResult> CoordinatorRejectWithResultAsync(
        Guid reportId, Guid coordinatorUserId, string reason,
        CancellationToken ct = default) =>
        RunMutationAsync(async () =>
        {
            var rejected = await CoordinatorRejectAsync(reportId, coordinatorUserId, reason, ct);
            return rejected
                ? ExpenseMutationResult.Success
                : ExpenseMutationResult.Failure("Could not reject the report. It may no longer be in Submitted status.");
        }, "Error coordinator-rejecting expense report {ReportId}", "Rejection failed", reportId);

    internal async Task<bool> ApproveAsync(
        Guid reportId, Guid actorUserId, Guid? overrideCategoryId, decimal? maxAmount,
        CancellationToken ct = default)
    {
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;

        var outboxEventId = Guid.NewGuid();
        var now = clock.GetCurrentInstant();
        var ok = await repo.ApproveAsync(
            reportId, actorUserId, overrideCategoryId, maxAmount, now, outboxEventId, ct);
        if (!ok) return false;

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

        return true;
    }

    public Task<ExpenseMutationResult> ApproveWithResultAsync(
        Guid reportId, Guid actorUserId, Guid? overrideCategoryId, decimal? maxAmount,
        CancellationToken ct = default) =>
        RunMutationAsync(async () =>
        {
            var approved = await ApproveAsync(reportId, actorUserId, overrideCategoryId, maxAmount, ct);
            return approved
                ? ExpenseMutationResult.Success
                : ExpenseMutationResult.Failure("Could not approve the report. It may not be in an approvable status.");
        }, "Error approving expense report {ReportId}", "Approval failed", reportId);

    internal async Task<bool> FinanceRejectAsync(
        Guid reportId, Guid actorUserId, string reason,
        CancellationToken ct = default)
    {
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return false;

        var now = clock.GetCurrentInstant();
        var ok = await repo.FinanceRejectAsync(reportId, actorUserId, reason, now, ct);
        if (!ok) return false;

        await auditLogService.LogAsync(
            AuditAction.ExpenseReject,
            AuditEntityTypes.Report, reportId,
            $"Finance rejected expense report: {reason}",
            actorUserId);

        return true;
    }

    public Task<ExpenseMutationResult> FinanceRejectWithResultAsync(
        Guid reportId, Guid actorUserId, string reason,
        CancellationToken ct = default) =>
        RunMutationAsync(async () =>
        {
            var rejected = await FinanceRejectAsync(reportId, actorUserId, reason, ct);
            return rejected
                ? ExpenseMutationResult.Success
                : ExpenseMutationResult.Failure("Could not reject the report. It may not be in a rejectable status.");
        }, "Error finance-rejecting expense report {ReportId}", "Rejection failed", reportId);

    public Task<int> CountFailedHoldedPushesAsync(CancellationToken ct = default)
        => repo.CountFailedOutboxAsync(ct);

    internal async Task<bool> RequeueHoldedPushAsync(
        Guid reportId, Guid actorUserId, CancellationToken ct = default)
    {
        var requeued = await repo.RequeueOutboxForReportAsync(reportId, ct);
        if (!requeued) return false;

        await auditLogService.LogAsync(
            AuditAction.ExpenseHoldedRequeued,
            AuditEntityTypes.Report, reportId,
            "Finance re-queued the Holded push.",
            actorUserId);

        return true;
    }

    public Task<ExpenseMutationResult> RequeueHoldedPushWithResultAsync(
        Guid reportId, Guid actorUserId, CancellationToken ct = default) =>
        RunMutationAsync(async () =>
        {
            var requeued = await RequeueHoldedPushAsync(reportId, actorUserId, ct);
            return requeued
                ? ExpenseMutationResult.Success
                : ExpenseMutationResult.Failure(
                    "This report has no failed or retrying Holded push to re-queue.");
        }, "Error re-queuing Holded push for expense report {ReportId}", "Re-queue failed", reportId);

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
                        await ProcessHoldedCreateAsync(
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
                        outboxEvent.Id, ex.Message, nextRetryAt, ct);
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
        await repo.MarkOutboxFailedPermanentlyAsync(
            outboxEvent.Id, error, clock.GetCurrentInstant(), ct);

        await auditLogService.LogAsync(
            AuditAction.ExpenseHoldedFailed,
            AuditEntityTypes.Report, outboxEvent.ExpenseReportId,
            $"Holded push failed permanently: {error}",
            OutboxJobName);
    }

    private async Task ProcessHoldedCreateAsync(
        Guid outboxEventId,
        ExpenseReportDto report,
        string submitterName,
        Instant now,
        CancellationToken ct)
    {
        // 1. Ensure the member's Holded creditor contact + binding (Finance owns creditor identity).
        //    Reuses the binding — including an admin's manual bind — or lazy-seeds from a cached
        //    contact id; never mints a duplicate. Legal name -> name; burner -> tradeName.
        string? burnerName = null;
        if (!string.IsNullOrWhiteSpace(report.PayeeName))
            burnerName = (await userService.GetUserInfoAsync(report.SubmitterUserId, ct))?.BurnerName;

        // This report carries a contact id only on a re-drain. Seeding from it alone misses a member
        // whose contact predates holded_creditor_contacts (that migration creates the table and
        // backfills nothing), leaving them with a contact on older reports and no binding row — and a
        // null seed makes Finance POST a *second* Holded contact, splitting their payables across two.
        // Lazy-seed from their most recent linked report instead; the push then writes the binding.
        var seedContactId = report.HoldedContactId;
        var seedAccountNum = report.HoldedSupplierAccountNum;
        if (string.IsNullOrEmpty(seedContactId))
        {
            var priorLinked = (await repo.GetForSubmitterAsync(report.SubmitterUserId, ct))
                .Where(r => r.Id != report.Id && !string.IsNullOrEmpty(r.HoldedContactId))
                .OrderByDescending(r => r.SubmittedAt ?? r.CreatedAt)
                .FirstOrDefault();
            seedContactId = priorLinked?.HoldedContactId;
            seedAccountNum = priorLinked?.HoldedSupplierAccountNum;
        }

        var holdedContactId = await holdedFinance.EnsureCreditorContactAsync(
            report.SubmitterUserId, report.PayeeName, burnerName, report.PayeeIban,
            seedContactId, seedAccountNum, ct);

        // Mirror the contact id onto the report (keeps the creditor-timeline reads working) before the
        // retryable doc-create + attachment steps. The supplier-account number is backfilled in step 4.
        await repo.SetHoldedContactLinkAsync(report.Id, holdedContactId, null, now, ct);

        // Books items[].account directly at doc creation (Peter, 2026-08-10: the account IS the
        // category — tags were a v1 workaround from before double-entry was understood). Null when
        // the category has no active mapping; the doc still creates, just unbooked.
        var holdedAccountId = await holdedFinance.GetHoldedAccountIdForCategoryAsync(report.BudgetCategoryId, ct);

        // Proof rows back an invoice line for review only — they are not booked and their
        // files are not uploaded. What Holded gets is the invoice itself.
        var bookableLines = report.Lines
            .Where(l => l.ParentLineId is null)
            .OrderBy(l => l.SortOrder)
            .ToList();

        var docLines = bookableLines
            .Select(l => new HoldedPurchaseDocumentLineInput
            {
                Description = l.Description,
                Amount = l.Amount,
                AccountId = holdedAccountId,
            })
            .ToList();

        // The receipts are booked in full and a negative line brings the doc down to the authorized
        // cap, so the payable matches what was approved without rewriting the receipt lines.
        if (report.Payable < report.Total)
        {
            docLines.Add(new HoldedPurchaseDocumentLineInput
            {
                Description =
                    $"Authorized maximum €{report.Payable.ToString("0.00", CultureInfo.InvariantCulture)} — adjustment",
                Amount = report.Payable - report.Total,
                AccountId = holdedAccountId,
            });
        }

        var input = new HoldedPurchaseDocumentInput
        {
            ContactId = holdedContactId,
            ContactName = submitterName,
            Date = report.SubmittedAt ?? report.CreatedAt,
            Description = report.Note ?? "",
            Lines = docLines,
        };

        // 2. Create the purchase doc (idempotent on HoldedDocId).
        string holdedDocId;
        if (string.IsNullOrEmpty(report.HoldedDocId))
        {
            holdedDocId = await holdedClient.CreatePurchaseDocumentAsync(input, ct);
            await repo.SetHoldedDocIdAsync(report.Id, holdedDocId, now, ct);
        }
        else
        {
            holdedDocId = report.HoldedDocId;
        }

        // 3. Upload attachments. Each upload is recorded so a re-run — after a failure partway
        // through this loop, or after a finance admin requeues the event — resumes instead of
        // adding a second copy of every earlier file to the same doc.
        foreach (var line in bookableLines)
        {
            if (line.AttachmentId is null || line.Attachment is null) continue;
            if (line.Attachment.HoldedUploadedAt is not null) continue;

            var bytes = await fileStorage.TryReadAsync(
                AttachmentKey(line.Attachment.Id, line.Attachment.Extension), ct);
            if (bytes is null)
                throw new InvalidOperationException(
                    $"Attachment file for {line.Attachment.Id}{line.Attachment.Extension} could not be read from storage.");
            using var stream = new MemoryStream(bytes, writable: false);
            await holdedClient.UploadAttachmentAsync(
                holdedDocId,
                new HoldedAttachmentInput
                {
                    FileName = line.Attachment.OriginalFileName,
                    ContentType = line.Attachment.ContentType,
                    Content = stream,
                },
                ct);
            await repo.MarkAttachmentPushedAsync(line.Attachment.Id, now, ct);
        }

        // 4. Resolve supplierRecord.num (now that a payable exists) and persist the contact link.
        // Best-effort: the doc is already created, so a failure here must NOT fail the outbox event
        // (that would strand a created doc as permanently-failed). There is no automatic retry — a null
        // num stays null until an admin runs POST /Finance/Creditors/Bind, or a later report for this
        // same member resolves it (nobodies-collective/Humans#972). ListCreditorAccountsAsync returns
        // such bindings in its Unresolved half, which is what makes the gap visible on
        // /Finance/Creditors so that manual step is discoverable.
        int? supplierAccountNum = null;
        try
        {
            var contact = await holdedClient.GetContactAsync(holdedContactId, ct);
            supplierAccountNum = contact.SupplierAccountNum;
        }
        catch (HoldedTransientException ex)
        {
            logger.LogWarning(
                "Could not resolve supplier account number for contact {ContactId}: {Error} — no automatic " +
                "retry; bind manually via POST /Finance/Creditors/Bind if it does not resolve on a later push",
                holdedContactId, ex.Message);
        }
        catch (HoldedPermanentException ex)
        {
            logger.LogWarning(
                "Permanent error resolving supplier account number for contact {ContactId}: {Error} — no " +
                "automatic retry; bind manually via POST /Finance/Creditors/Bind",
                holdedContactId, ex.Message);
        }
        await repo.SetHoldedContactLinkAsync(report.Id, holdedContactId, supplierAccountNum, now, ct);
        if (supplierAccountNum is not null)
            await holdedFinance.SetCreditorAccountNumAsync(report.SubmitterUserId, supplierAccountNum.Value, ct);

        await repo.MarkOutboxProcessedAsync(outboxEventId, now, ct);

        await auditLogService.LogAsync(
            AuditAction.ExpenseHoldedPushed,
            AuditEntityTypes.Report, report.Id,
            $"Pushed to Holded as purchase document {holdedDocId}.",
            OutboxJobName);
    }

    private async Task<ExpenseReportDto> RequireEditableReportAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct)
    {
        var report = await repo.GetByIdAsync(reportId, ct)
            ?? throw new ExpenseValidationException("Report not found.");
        if (report.SubmitterUserId != submitterUserId)
            throw new UnauthorizedAccessException("Only the submitter can edit lines.");
        // Line mutations are Draft-only — submitted reports are frozen for review.
        if (report.Status is not ExpenseReportStatus.Draft)
            throw new ExpenseValidationException(
                $"Lines cannot be edited when the report is in status {report.Status}.");
        return report;
    }

    private async Task RequireCoordinatorForCategoryAsync(
        Guid categoryId, Guid actorUserId, CancellationToken ct)
    {
        var category = await budgetService.GetCategoryByIdAsync(categoryId);
        if (category is null)
            throw new InvalidOperationException("Budget category not found.");
        if (!category.TeamId.HasValue)
            throw new UnauthorizedAccessException(
                "Category has no owning team; coordinator endorsement is not valid.");
        var isCoordinator = await teamService.IsUserCoordinatorOfTeamAsync(
            category.TeamId.Value, actorUserId, ct);
        if (!isCoordinator)
            throw new UnauthorizedAccessException("Actor is not a coordinator of the category's team.");
    }

    /// <summary>User's reports (lines+attachment metadata), masked IBAN, audit. Chain-follows merge tombstones.</summary>
    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(
        Guid userId, CancellationToken ct)
    {
        var sourceIds = await userService.GetMergedSourceIdsAsync(userId, ct);

        var allIds = new List<Guid>(sourceIds.Count + 1);
        allIds.AddRange(sourceIds);
        allIds.Add(userId);

        var allReports = new List<ExpenseReportDto>();
        foreach (var id in allIds)
        {
            var reports = await repo.GetForSubmitterAsync(id, ct);
            allReports.AddRange(reports);
        }

        var profile = (await userService.GetUserInfoAsync(userId, ct))?.Profile;
        var maskedIban = string.IsNullOrEmpty(profile?.Iban)
            ? null
            : IbanFormatter.Mask(profile.Iban);

        var expenseActions = new List<AuditAction>
        {
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
                r.SubmittedAt,
                r.ApprovedAt,
                r.CreatedAt,
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
            new UserDataSlice(GdprExportSections.ExpenseReports,
                shapedReports.Count > 0 ? shapedReports : null),
            new UserDataSlice(GdprExportSections.ExpenseAuditLog,
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
            [GdprExportSections.ExpenseReports] = FiscalRetention,
            [GdprExportSections.ExpenseAuditLog] = FiscalRetention
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
}
