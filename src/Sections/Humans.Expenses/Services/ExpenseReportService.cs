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
using System.Transactions;
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
    IUserEmailService userEmailService,
    IEmailService emailService,
    ExpensesEmails emails,
    IAuditLogService auditLogService,
    IHoldedService holded,
    IHoldedFinanceService holdedFinance,
    IFinanceService finance,
    IFinanceWrite financeWrite,
    IPreapprovalService preapprovals,
    IPreapprovalWrite preapprovalWrites,
    IClock clock,
    ILogger<ExpenseReportService> logger,
    IOptions<TravelReimbursementConfig> travelConfig) : IExpenseReportManagement,
        IUserDataContributor
{
    internal const string ExpenseReports = "ExpenseReports";
    internal const string ExpenseAuditLog = "ExpenseAuditLog";

    private readonly TravelReimbursementConfig _travel = travelConfig.Value;

    /// <summary>Claim deadlines are Europe/Madrid dates.</summary>
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

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
    /// The creditor balance already sums all of a member's outstanding docs, so whatever exceeds their own
    /// registered-unpaid report totals shows as fronted amounts and adjustments.
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

    public async Task<IReadOnlyList<ExpenseReportDto>> GetForSubmitterAsync(
        Guid submitterUserId, CancellationToken ct = default)
    {
        var submitter = await userService.GetUserInfoAsync(submitterUserId, ct);
        var reports = new List<ExpenseReportDto>();
        foreach (var id in submitter?.AllUserIds ?? [submitterUserId])
            reports.AddRange(await repo.GetForSubmitterAsync(id, ct));
        return reports;
    }

    public async Task<IReadOnlyList<ExpenseReportDto>> GetReviewQueueAsync(
        Guid viewerUserId, bool isFinanceAdmin, CancellationToken ct = default)
    {
        var queue = await repo.GetForReviewQueueAsync(ct);
        if (isFinanceAdmin) return queue;

        // Filtering the whole queue in memory beats a per-audience query at this scale.
        // Drafts and withdrawals are excluded upstream for everyone.
        var categoryIds = await GetCoordinatorCategoryIdsAsync(viewerUserId, ct);
        var managedGrantIds = await preapprovals.ListManagedGrantIdsAsync(viewerUserId, ct);
        var viewer = await userService.GetUserInfoAsync(viewerUserId, ct);
        var ownIds = viewer?.AllUserIds ?? [viewerUserId];
        return queue
            .Where(r => ownIds.Contains(r.SubmitterUserId)
                        || (r.BudgetCategoryId is { } categoryId && categoryIds.Contains(categoryId))
                        || (r.PreapprovalGrantId is { } grantId && managedGrantIds.Contains(grantId)))
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
        var managedGrantIds = await preapprovals.ListManagedGrantIdsAsync(coordinatorUserId, ct);

        var queue = categoryIds.Count == 0
            ? []
            : (await repo.GetByCategoryIdsAndStatusAsync(categoryIds, ExpenseReportStatus.Submitted, ct)).ToList();
        if (managedGrantIds.Count > 0)
            queue.AddRange((await repo.GetForReviewQueueAsync(ct)).Where(r =>
                r.Status == ExpenseReportStatus.Submitted
                && r.PreapprovalGrantId is { } grantId && managedGrantIds.Contains(grantId)));
        return queue;
    }

    private async Task<IReadOnlyList<Guid>> GetCoordinatorCategoryIdsAsync(
        Guid coordinatorUserId, CancellationToken ct)
    {
        var year = await budgetService.GetActiveYearAsync(ct);
        if (year is null) return [];

        // The same rule that grants endorse: whoever can sign a claim off sees it.
        var categories = year.Groups.SelectMany(g => g.Categories).Where(c => c.TeamId.HasValue).ToList();
        var coordinatedTeamIds = new HashSet<Guid>();
        foreach (var teamId in categories.Select(c => c.TeamId!.Value).Distinct())
        {
            if (await teamService.IsUserCoordinatorOfTeamAsync(teamId, coordinatorUserId, ct))
                coordinatedTeamIds.Add(teamId);
        }

        return categories
            .Where(c => coordinatedTeamIds.Contains(c.TeamId!.Value))
            .Select(c => c.Id)
            .ToList();
    }

    public async Task<ExpenseCreateDraftResult> CreateDraftAsync(
        Guid submitterUserId, Guid actorUserId, int holdedAccountNumber, string? note,
        Guid? preapprovalGrantId = null,
        CancellationToken ct = default)
    {
        Guid? reportId = null;
        var result = await RunMutationAsync(ct, async () =>
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
            await repo.AddDraftAsync(report, ct);
            reportId = report.Id;

            // Self-created drafts stay unaudited — the report itself is the record. A report filed for
            // somebody else is an action taken on their behalf, so it leaves a trail naming both.
            if (actorUserId != submitterUserId)
            {
                await auditLogService.LogAsync(
                    ExpensesAuditActions.ExpenseCreatedOnBehalf,
                    AuditEntityTypes.Report, report.Id,
                    $"Created expense report on behalf of {await DescribeMemberAsync(submitterUserId, ct)}."
                    + DescribeGrant(preapprovalGrantId),
                    actorUserId,
                    relatedEntityId: submitterUserId,
                    relatedEntityType: AuditEntityTypes.User);
            }

            return ExpenseMutationResult.Success;
        }, "Error creating draft expense report for {SubmitterUserId}", submitterUserId);

        return new ExpenseCreateDraftResult(result.Succeeded, result.Error, reportId);
    }

    private static string DescribeGrant(Guid? grantId) =>
        grantId is { } id ? $" Filed against pre-approval {id}." : "";

    /// <summary>
    /// The grant's account, with no category or year, which routes the report past endorsement. The grant must be
    /// claimable by the submitter today; the one a draft already holds (<paramref name="heldGrantId"/>) is always kept,
    /// so a rejected report can be corrected after the deadline it met.
    /// </summary>
    private async Task<(ExpenseBooking? Booking, ExpenseMutationResult? Refusal)> ResolveGrantBookingAsync(
        Guid grantId, Guid submitterUserId, Guid? heldGrantId, CancellationToken ct)
    {
        var grant = grantId == heldGrantId
            ? await preapprovals.GetGrantAsync(grantId, ct)
            : (await preapprovals.ListClaimableForUserAsync(submitterUserId, ct)).FirstOrDefault(g => g.Id == grantId);
        if (grant is null)
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_PreapprovalNotClaimable"));
        return (new ExpenseBooking(grant.HoldedAccountNumber, grant.HoldedAccountId, null, null), null);
    }

    /// <summary>Best-effort display name for an audit of an already committed edit.</summary>
    private async Task<string> DescribeMemberAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            return (await userService.GetUserInfoAsync(userId, ct))?.BurnerName ?? userId.ToString();
        }
        catch (Exception ex)
        {
            // Name enrichment must not skip the audit or fail an edit that already committed.
            logger.LogWarning(ex, "Could not resolve audit display name for member {UserId}", userId);
            return userId.ToString();
        }
    }

    /// <summary>
    /// An edit to somebody else's report owes them a trail naming both; a member editing their own leaves none.
    /// <paramref name="whatChanged"/> is the sentence opener, e.g. "Added line 'Fuel'".
    /// </summary>
    private async Task AuditOnBehalfEditAsync(
        ExpenseReportDto report, Guid actorUserId, string whatChanged, CancellationToken ct)
    {
        if (actorUserId == report.SubmitterUserId) return;

        await auditLogService.LogAsync(
            ExpensesAuditActions.ExpenseEditedOnBehalf,
            AuditEntityTypes.Report, report.Id,
            $"{whatChanged} on behalf of {await DescribeMemberAsync(report.SubmitterUserId, ct)}.",
            actorUserId,
            relatedEntityId: report.SubmitterUserId,
            relatedEntityType: AuditEntityTypes.User);
    }

    private static string DescribeNote(string? note) =>
        string.IsNullOrWhiteSpace(note) ? "cleared" : $"\"{note}\"";

    public Task<ExpenseMutationResult> UpdateDraftWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin,
        int holdedAccountNumber, string? note,
        Guid? preapprovalGrantId = null,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var (editableReport, refusal) = await GetEditableReportAsync(reportId, actorUserId, actorIsFinanceAdmin, ct);
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
                var pinnedYearId = IsPendingApproval(report.Status) ? report.BudgetYearId : null;
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
                && (await preapprovals.GetGrantAsync(held, ct))?.Status == GrantStatus.Claimed
                ? held
                : (Guid?)null;
            if (released is { } releasedId)
            {
                using var scope = new TransactionScope(
                    TransactionScopeOption.Required,
                    new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
                    TransactionScopeAsyncFlowOption.Enabled);
                var release = await preapprovalWrites.ReleaseAsync(releasedId, reportId, actorUserId, ct);
                if (release.Refusal is { } releaseRefusal)
                {
                    logger.LogWarning("Pre-approval {GrantId} refused release by report {ReportId}: {Reason}", releasedId, reportId, releaseRefusal);
                    return ExpenseMutationResult.Failure("Expenses_Validation_PreapprovalUnavailable");
                }
                await repo.UpdateDraftAsync(updated, ct);
                scope.Complete();
            }
            else
            {
                await repo.UpdateDraftAsync(updated, ct);
            }

            await AuditOnBehalfEditAsync(report, actorUserId,
                $"Updated header (account {booking.HoldedAccountNumber}, subject {DescribeNote(note)})"
                + (preapprovalGrantId != report.PreapprovalGrantId
                    ? $" and pre-approval ({report.PreapprovalGrantId?.ToString() ?? "none"} → {preapprovalGrantId?.ToString() ?? "none"})"
                    : ""), ct);

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
            // This path takes free-text amounts; travel lines are computed.
            if (lineType is not (ExpenseLineType.Receipt or ExpenseLineType.Invoice))
                return Reject(new("Expenses_Validation_OnlyReceiptAndInvoiceLines", []));
            if (decimal.Round(amount, 2) != amount)
                return Reject(new("Expenses_Validation_AmountPrecision", []));
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

            // One atomic save removes the line, its proof rows and their attachment rows; files go only after the
            // commit (an orphan file is a warning, an orphan row is a bug).
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
                    logger.LogWarning(ex, "Could not delete unlinked attachment file {AttachmentId}", attachmentId);
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
                relatedEntityType: AuditEntityTypes.User);
        }
        catch
        {
            var metadataRemoved = true;
            try
            {
                // Restore metadata, linkage and any decline/total change atomically, including when the failed
                // save committed before throwing. Keep the old file until audit succeeds.
                var rollback = await repo.ReplaceLineAttachmentAsync(
                    reportId, lineId, previousAttachment, CancellationToken.None,
                    declineState: line.Declined
                        ? (report.UpdatedAt, line.DeclinedReason, line.DeclinedByUserId, line.DeclinedAt)
                        : null);
                metadataRemoved = rollback.Found;
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

        if (replacedAttachment is not null)
        {
            try
            {
                await fileStorage.DeleteAsync(
                    AttachmentKey(replacedAttachment.Id, replacedAttachment.Extension), CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not delete superseded attachment file {AttachmentId}", replacedAttachment.Id);
            }
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

            var removed = await repo.RemoveLineAttachmentAsync(reportId, lineId, ct);
            if (removed is null) return ExpenseMutationResult.Success;

            await auditLogService.LogAsync(
                ExpensesAuditActions.ExpenseAttachmentRemoved,
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
                    AttachmentKey(removed.Id, removed.Extension), CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not delete attachment file {AttachmentId} for line {LineId}",
                    removed.Id, lineId);
            }

            return ExpenseMutationResult.Success;
        }, "Error removing attachment from line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> SubmitWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin, CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var report = await repo.GetByIdAsync(reportId, ct);
            if (report is null) return ExpenseMutationResult.Failure("Expenses_Validation_CouldNotSubmitReport");
            if (!actorIsFinanceAdmin && !await IsSubmitterAsync(report, actorUserId, ct))
                return ExpenseMutationResult.Failure("Expenses_Validation_OnlySubmitterCanSubmit");
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
            var profile = (await userService.GetUserInfoAsync(report.SubmitterUserId, ct))?.Profile;
            if (await finance.GetIbanAsync(report.SubmitterUserId, ct) is not { Length: > 0 } payeeIban)
                return ExpenseMutationResult.Failure("Expenses_Validation_SubmitterNeedsIban");

            // Financial records use the legal name, not BurnerName.
            if (string.IsNullOrWhiteSpace(profile?.FirstName) || string.IsNullOrWhiteSpace(profile.LastName))
            {
                return ExpenseMutationResult.Failure("Expenses_Validation_SubmitterNeedsFirstAndLastName");
            }
            var legalName = $"{profile.FirstName} {profile.LastName}".Trim();

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

                using var scope = new TransactionScope(
                    TransactionScopeOption.Required,
                    new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
                    TransactionScopeAsyncFlowOption.Enabled);
                var claim = await preapprovalWrites.ClaimAsync(grantId, reportId, actorUserId, ct);
                if (claim.Refusal is { } refusal)
                {
                    logger.LogWarning("Pre-approval {GrantId} refused the claim by report {ReportId}: {Reason}", grantId, reportId, refusal);
                    return ExpenseMutationResult.Failure("Expenses_Validation_PreapprovalUnavailable");
                }
                var stamp = claim.Stamp!;
                var ok = await repo.SubmitAsync(
                    reportId, legalName, payeeIban, now,
                    grantStamp: new ExpenseBooking(stamp.HoldedAccountNumber, stamp.HoldedAccountId, null, null),
                    grantMaxAmount: stamp.MaxAmount,
                    ct: ct);
                if (!ok) return ExpenseMutationResult.Failure("Expenses_Validation_CouldNotSubmitReport");
                scope.Complete();
            }

            await auditLogService.LogAsync(
                ExpensesAuditActions.ExpenseSubmit,
                AuditEntityTypes.Report, reportId,
                (report.SubmitterUserId == actorUserId
                    ? "Submitted expense report."
                    : $"Submitted expense report on behalf of {await DescribeMemberAsync(report.SubmitterUserId, ct)}.")
                + DescribeGrant(report.PreapprovalGrantId),
                actorUserId,
                relatedEntityId: report.SubmitterUserId,
                relatedEntityType: AuditEntityTypes.User);

            return ExpenseMutationResult.Success;

        }, "Error submitting expense report {ReportId}", reportId);

    /// <summary>
    /// A member-facing refusal, or null when the claim should be attempted. Mirrors
    /// <see cref="IPreapprovalWrite.ClaimAsync"/>: the report already holding the grant resubmits past the deadline.
    /// </summary>
    private async Task<ExpenseMutationResult?> GetGrantClaimRefusalAsync(Guid grantId, Guid reportId, CancellationToken ct)
    {
        var grant = await preapprovals.GetGrantAsync(grantId, ct);
        switch (grant?.Status)
        {
            case GrantStatus.Revoked:
                return ExpenseMutationResult.Failure("Expenses_Validation_PreapprovalRevoked");
            case GrantStatus.Open:
                var today = clock.GetCurrentInstant().InZone(MadridZone).Date;
                return grant.ClaimBy < today
                    ? ExpenseMutationResult.Failure("Expenses_Validation_PreapprovalDeadlinePassed", grant.ClaimBy.ToInvariantDate())
                    : null;
            case GrantStatus.Claimed:
                // Held by this report (a resubmit) or by another one; ClaimAsync tells them apart.
                return null;
            default:
                return ExpenseMutationResult.Failure("Expenses_Validation_PreapprovalUnavailable");
        }
    }

    public Task<ExpenseMutationResult> WithdrawWithResultAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var report = await repo.GetByIdAsync(reportId, ct);
            if (report is null) return ExpenseMutationResult.Failure("Expenses_Flash_WithdrawFailed");
            if (!await IsSubmitterAsync(report, submitterUserId, ct))
                return ExpenseMutationResult.Failure("Expenses_Validation_OnlySubmitterCanWithdraw");

            // Approval queues the Holded push, so only a report still pending approval can be
            // withdrawn; the repository refuses anything else too.
            if (!IsPendingApproval(report.Status))
                return ExpenseMutationResult.Failure("Expenses_Flash_WithdrawFailed");

            var now = clock.GetCurrentInstant();
            // A held pre-approval goes back to the member. A grant revoked while pending is no longer held and is left
            // alone, so the withdrawal still goes through.
            var heldGrant = report.PreapprovalGrantId is { } grantId
                ? await preapprovals.GetGrantAsync(grantId, ct)
                : null;
            var releasesGrant = heldGrant?.Status == GrantStatus.Claimed;
            if (!releasesGrant)
            {
                var ok = await repo.WithdrawAsync(reportId, now, ct);
                if (!ok) return ExpenseMutationResult.Failure("Expenses_Flash_WithdrawFailed");
            }
            else
            {
                using var scope = new TransactionScope(
                    TransactionScopeOption.Required,
                    new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
                    TransactionScopeAsyncFlowOption.Enabled);
                var release = await preapprovalWrites.ReleaseAsync(report.PreapprovalGrantId!.Value, reportId, submitterUserId, ct);
                if (release.Refusal is { } releaseRefusal)
                {
                    logger.LogWarning("Pre-approval {GrantId} refused release by report {ReportId}: {Reason}", report.PreapprovalGrantId, reportId, releaseRefusal);
                    return ExpenseMutationResult.Failure("Expenses_Flash_WithdrawFailed");
                }
                var ok = await repo.WithdrawAsync(reportId, now, ct);
                if (!ok) return ExpenseMutationResult.Failure("Expenses_Flash_WithdrawFailed");
                scope.Complete();
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

    /// <summary>Submitted, not yet approved: has a payee snapshot and a booked year, but is not final.</summary>
    private static bool IsPendingApproval(ExpenseReportStatus status) =>
        status is ExpenseReportStatus.Submitted or ExpenseReportStatus.CoordinatorEndorsed;

    private async Task RefreshPayeeIbanSnapshotAsync(
        ExpenseReportDto report, Guid actorUserId, string normalizedIban, CancellationToken ct)
    {
        if (string.Equals(report.PayeeIban, normalizedIban, StringComparison.Ordinal)) return;

        var updated = await repo.UpdatePayeeIbanAsync(
            report.Id, normalizedIban, clock.GetCurrentInstant(), ct);
        if (!updated) return;

        // On the report so it lands in the history the correcting admin is looking at. Unmasked when set for another
        // member, as above.
        var description = actorUserId == report.SubmitterUserId
            ? "Payee IBAN updated"
            : $"Payee IBAN updated for {await DescribeMemberAsync(report.SubmitterUserId, ct)} to {normalizedIban}";

        await auditLogService.LogAsync(
            ExpensesAuditActions.ExpensePayeeIbanUpdated,
            AuditEntityTypes.Report, report.Id,
            $"{description}.",
            actorUserId,
            relatedEntityId: report.SubmitterUserId,
            relatedEntityType: AuditEntityTypes.User);
    }

    /// <summary>
    /// A member's own change stays "IBAN set" / "IBAN removed". When somebody else makes it, the entry names the member
    /// and carries the IBAN <b>unmasked</b>: audit may hold its subject's PII, and only what was typed can trace a
    /// wrong IBAN to its typist. The one exception to masking IBANs.
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

    private static string MaxAmountDetail(decimal? maxAmount) =>
        maxAmount is { } cap
            ? $" Authorized maximum {cap.ToString("0.00", CultureInfo.InvariantCulture)} EUR."
            : "";

    private async Task<(decimal? Amount, ExpenseMutationResult? Refusal)> ResolveReviewCapAsync(
        Guid? grantId, decimal? requestedAmount, CancellationToken ct)
    {
        if (requestedAmount is { } amount && decimal.Round(amount, 2) != amount)
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_AmountPrecision"));

        if (grantId is not { } id) return (requestedAmount, null);

        var grant = await preapprovals.GetGrantAsync(id, ct);
        if (grant is null)
            return (null, ExpenseMutationResult.OperatorFailure("The report's pre-approval no longer exists; reject the report."));
        if (requestedAmount > grant.MaxAmount)
            return (null, ExpenseMutationResult.OperatorFailure(
                $"The cap cannot exceed the pre-approved {grant.MaxAmount.ToString("0.00", CultureInfo.InvariantCulture)} EUR."));

        return (requestedAmount ?? grant.MaxAmount, null);
    }

    public Task<ExpenseMutationResult> CoordinatorEndorseWithResultAsync(
        Guid reportId, Guid coordinatorUserId, bool actorIsFinanceAdmin, decimal? maxAmount,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var report = await repo.GetByIdAsync(reportId, ct);
            if (report is null) return ExpenseMutationResult.OperatorFailure("Could not endorse the report. It may no longer be in Submitted status.");

            if (!actorIsFinanceAdmin && await GetFirstReviewerRefusalAsync(report, coordinatorUserId, ct) is { } refusal)
                return refusal;

            var cap = await ResolveReviewCapAsync(report.PreapprovalGrantId, maxAmount, ct);
            if (cap.Refusal is { } capRefusal) return capRefusal;
            maxAmount = cap.Amount;

            var now = clock.GetCurrentInstant();
            var ok = await repo.CoordinatorEndorseAsync(reportId, coordinatorUserId, maxAmount, now, ct);
            if (!ok) return ExpenseMutationResult.OperatorFailure("Could not endorse the report. It may no longer be in Submitted status.");

            await auditLogService.LogAsync(
                ExpensesAuditActions.ExpenseEndorse,
                AuditEntityTypes.Report, reportId,
                ReviewerLabel(report, actorIsFinanceAdmin)
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

            if (!actorIsFinanceAdmin && await GetFirstReviewerRefusalAsync(report, coordinatorUserId, ct) is { } refusal)
                return refusal;

            var now = clock.GetCurrentInstant();
            var ok = await repo.CoordinatorRejectAsync(reportId, coordinatorUserId, reason, now, ct);
            if (!ok) return ExpenseMutationResult.OperatorFailure("Could not reject the report. It may no longer be in Submitted status.");

            await auditLogService.LogAsync(
                ExpensesAuditActions.ExpenseCoordinatorReject,
                AuditEntityTypes.Report, reportId,
                $"{ReviewerLabel(report, actorIsFinanceAdmin)} rejected expense report: {reason}",
                coordinatorUserId);

            return ExpenseMutationResult.Success;

        }, "Error coordinator-rejecting expense report {ReportId}", reportId);

    public Task<ExpenseMutationResult> DeclineLineWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin, Guid lineId, string reason,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var trimmed = reason?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return ExpenseMutationResult.OperatorFailure("A reason is required to decline a line.");
            if (trimmed.Length > DeclineReasonMaxLength)
                return ExpenseMutationResult.OperatorFailure(
                    $"The reason can be at most {DeclineReasonMaxLength} characters.");

            var (report, line, refusal) = await GetDeclinableLineAsync(
                reportId, actorUserId, actorIsFinanceAdmin, lineId, ct);
            if (refusal is not null) return refusal;
            if (line!.Declined)
                return ExpenseMutationResult.OperatorFailure("That line is already declined.");

            var ok = await repo.SetLineDeclinedAsync(
                reportId, lineId, declined: true, trimmed, actorUserId, clock.GetCurrentInstant(), ct);
            if (!ok) return ExpenseMutationResult.OperatorFailure("Could not decline the line.");

            await auditLogService.LogAsync(
                ExpensesAuditActions.ExpenseLineDeclined,
                AuditEntityTypes.Report, reportId,
                $"{ReviewerLabel(report!, actorIsFinanceAdmin)} declined line "
                    + $"\"{line.Description}\" ({line.Amount.ToString("0.00", CultureInfo.InvariantCulture)} EUR): {trimmed}",
                actorUserId);

            return ExpenseMutationResult.Success;
        }, "Error declining line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> UndoDeclineLineWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin, Guid lineId,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var (report, line, refusal) = await GetDeclinableLineAsync(
                reportId, actorUserId, actorIsFinanceAdmin, lineId, ct);
            if (refusal is not null) return refusal;
            if (!line!.Declined)
                return ExpenseMutationResult.OperatorFailure("That line is not declined.");

            var ok = await repo.SetLineDeclinedAsync(
                reportId, lineId, declined: false, reason: null, byUserId: null, clock.GetCurrentInstant(), ct);
            if (!ok) return ExpenseMutationResult.OperatorFailure("Could not undo the decline.");

            await auditLogService.LogAsync(
                ExpensesAuditActions.ExpenseLineDeclineUndone,
                AuditEntityTypes.Report, reportId,
                $"{ReviewerLabel(report!, actorIsFinanceAdmin)} undid the decline of line "
                    + $"\"{line.Description}\" ({line.Amount.ToString("0.00", CultureInfo.InvariantCulture)} EUR); "
                    + $"it had been declined: {line.DeclinedReason}",
                actorUserId);

            return ExpenseMutationResult.Success;
        }, "Error undoing decline of line {LineId} on report {ReportId}", lineId, reportId);

    private const int DeclineReasonMaxLength = 1000;

    /// <summary>
    /// The service half of the DeclineLine gate: the report must still await a decision (a first reviewer only while
    /// Submitted, a finance admin through CoordinatorEndorsed), and the line must be top-level. Proof rows are never
    /// declinable.
    /// </summary>
    private async Task<(ExpenseReportDto? Report, ExpenseLineDto? Line, ExpenseMutationResult? Refusal)>
        GetDeclinableLineAsync(
            Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin, Guid lineId, CancellationToken ct)
    {
        const string cannot = "Could not change the line. The report may no longer be awaiting a decision.";
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return (null, null, ExpenseMutationResult.OperatorFailure(cannot));

        var open = actorIsFinanceAdmin
            ? report.Status is ExpenseReportStatus.Submitted or ExpenseReportStatus.CoordinatorEndorsed
            : report.Status is ExpenseReportStatus.Submitted;
        if (!open) return (null, null, ExpenseMutationResult.OperatorFailure(cannot));

        if (!actorIsFinanceAdmin
            && await GetFirstReviewerRefusalAsync(report, actorUserId, ct) is { } reviewerRefusal)
            return (null, null, reviewerRefusal);

        var line = report.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null) return (null, null, ExpenseMutationResult.Failure("Expenses_Validation_LineNotOnReport"));
        if (line.ParentLineId is not null)
            return (null, null, ExpenseMutationResult.OperatorFailure("Proof rows cannot be declined; decline the invoice line instead."));
        return (report, line, null);
    }

    /// <summary>
    /// Re-reads the report so the amount is the DTO's own <c>Payable</c>, cap included. Sent after the save: the outbox
    /// row is a promise of money, and a failed approval must not make it.
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
            submitter.Id,
            recipient, submitter.BurnerName, reportId, approved.Payable,
            IbanFormatter.Mask(approved.PayeeIban),
            language.IsSupportedCultureCode() ? language : CultureCatalog.DefaultCultureCode), ct);
    }

    public Task<ExpenseMutationResult> ApproveWithResultAsync(
        Guid reportId, Guid actorUserId, int? overrideAccountNumber, decimal? maxAmount,
        CancellationToken ct = default) =>
        RunMutationAsync(ct, async () =>
        {
            var report = await repo.GetByIdAsync(reportId, ct);
            if (report is null) return ExpenseMutationResult.OperatorFailure("Could not approve the report. It may not be in an approvable status.");

            // Re-booking at approval keeps the report's year: a category account from another year
            // is refused the same way a header edit would refuse it.
            ExpenseBooking? overrideBooking = null;
            if (overrideAccountNumber is { } overrideNumber && overrideNumber != report.HoldedAccountNumber)
            {
                if (report.PreapprovalGrantId is not null)
                    return ExpenseMutationResult.OperatorFailure("The account is locked by the report's pre-approval; reject the report to change it.");
                var (resolved, refusal) = await ResolveBookingAsync(overrideNumber, report.BudgetYearId, ct);
                if (refusal is not null) return refusal;
                overrideBooking = resolved;
            }

            var cap = await ResolveReviewCapAsync(report.PreapprovalGrantId, maxAmount, ct);
            if (cap.Refusal is { } capRefusal) return capRefusal;
            maxAmount = cap.Amount;

            var outboxEventId = Guid.NewGuid();
            var now = clock.GetCurrentInstant();
            bool ok;
            if (report.PreapprovalGrantId is not { } consumedGrantId)
            {
                ok = await repo.ApproveAsync(
                    reportId, actorUserId, overrideBooking, maxAmount, now, outboxEventId, ct);
            }
            else
            {
                using var scope = new TransactionScope(
                    TransactionScopeOption.Required,
                    new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
                    TransactionScopeAsyncFlowOption.Enabled);
                var consumption = await preapprovalWrites.ConsumeAsync(consumedGrantId, reportId, actorUserId, ct);
                if (consumption.Refusal is { } refusal)
                {
                    logger.LogWarning("Pre-approval {GrantId} refused consumption by report {ReportId}: {Reason}", consumedGrantId, reportId, refusal);
                    return ExpenseMutationResult.OperatorFailure("The report's pre-approval was revoked or is no longer held by it; reject the report.");
                }
                ok = await repo.ApproveAsync(
                    reportId, actorUserId, overrideBooking, maxAmount, now, outboxEventId, ct);
                if (ok) scope.Complete();
            }
            if (!ok) return ExpenseMutationResult.OperatorFailure("Could not approve the report. It may not be in an approvable status.");

            await auditLogService.LogAsync(
                ExpensesAuditActions.ExpenseApprove,
                AuditEntityTypes.Report, reportId,
                "Finance approved expense report." + MaxAmountDetail(maxAmount)
                + (report.PreapprovalGrantId is { } g ? $" Consumed pre-approval {g}." : ""),
                actorUserId);

            if (overrideBooking is not null)
            {
                await auditLogService.LogAsync(
                    ExpensesAuditActions.ExpenseCategoryOverride,
                    AuditEntityTypes.Report, reportId,
                    $"Account overridden during approval from {report.HoldedAccountNumber} to {overrideBooking.HoldedAccountNumber}"
                    + (overrideBooking.BudgetCategoryId is { } overrideCategory
                        ? $" (budget category {overrideCategory})."
                        : " (no budget category)."),
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
                ExpensesAuditActions.ExpenseReject,
                AuditEntityTypes.Report, reportId,
                $"Finance rejected expense report: {reason}",
                actorUserId);

            return ExpenseMutationResult.Success;

        }, "Error finance-rejecting expense report {ReportId}", reportId);

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

    /// <summary>
    /// The service half of the edit gate. A member edits only their own Draft. A finance admin edits on their behalf,
    /// so ownership is waived and the window is Draft, Submitted and CoordinatorEndorsed.
    /// </summary>
    private async Task<(ExpenseReportDto? Report, ExpenseMutationResult? Refusal)> GetEditableReportAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin, CancellationToken ct)
    {
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null)
            return (null, ExpenseMutationResult.Failure("Expenses_Iban_ReportNotFound"));
        if (!actorIsFinanceAdmin && !await IsSubmitterAsync(report, actorUserId, ct))
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_OnlySubmitterCanEdit"));

        var editable = actorIsFinanceAdmin
            ? report.Status is ExpenseReportStatus.Draft or ExpenseReportStatus.Submitted or ExpenseReportStatus.CoordinatorEndorsed
            : report.Status is ExpenseReportStatus.Draft;
        return editable
            ? (report, null)
            : (null, ExpenseMutationResult.Failure("Expenses_Validation_ReportCannotBeEditedInStatus", report.Status));
    }

    private async Task<bool> IsSubmitterAsync(ExpenseReportDto report, Guid actorUserId, CancellationToken ct) =>
        report.SubmitterUserId == actorUserId
        || (await userService.GetUserInfoAsync(actorUserId, ct))?.AllUserIds.Contains(report.SubmitterUserId) == true;

    /// <summary>
    /// Resolves what a report books to from the account the submitter picked. A budget-category
    /// account also carries the category it maps to and that category's year; any other account
    /// carries neither, which is what routes the report past coordinator endorsement. A draft takes
    /// the active year's categories; a report past submit (<paramref name="pinnedYearId"/> set)
    /// accepts only its own year's, so a header edit never moves its accounting year.
    /// </summary>
    private async Task<(ExpenseBooking? Booking, ExpenseMutationResult? Refusal)> ResolveBookingAsync(
        int holdedAccountNumber, Guid? pinnedYearId, CancellationToken ct)
    {
        var account = (await holdedFinance.ListExpenseAccountsAsync(activeOnly: true, ct))
            .FirstOrDefault(a => a.AccountNum == holdedAccountNumber);
        if (account is null)
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_AccountNotFound"));
        if (!account.IsBudgetCategory)
            return (new ExpenseBooking(account.AccountNum, account.AccountId, null, null), null);

        // The option list said this account is a budget category, so the map row exists; the
        // category behind it may still be gone or belong to another year.
        var mapRow = (await holdedFinance.GetCategoryMapAsync(ct))
            .FirstOrDefault(m => m.IsActive && m.HoldedAccountNumber == holdedAccountNumber);
        if (mapRow is null)
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_CategoryNotFound"));

        if (pinnedYearId is { } pinned)
        {
            var snapshot = await budgetService.GetCategoryByIdAsync(mapRow.BudgetCategoryId);
            if (snapshot is null)
                return (null, ExpenseMutationResult.Failure("Expenses_Validation_CategoryNotFound"));
            if (snapshot.BudgetGroup?.BudgetYearId != pinned)
                return (null, ExpenseMutationResult.Failure("Expenses_Validation_CategoryDifferentBudgetYear"));
            return (new ExpenseBooking(account.AccountNum, account.AccountId, snapshot.Id, pinned), null);
        }

        var activeYear = await budgetService.GetActiveYearAsync()
            ?? throw new InvalidOperationException("No active budget year.");
        var inActiveYear = activeYear.Groups.SelectMany(g => g.Categories).Any(c => c.Id == mapRow.BudgetCategoryId);
        if (!inActiveYear)
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_CategoryNotFound"));
        return (new ExpenseBooking(account.AccountNum, account.AccountId, mapRow.BudgetCategoryId, activeYear.Id), null);
    }

    private static string ReviewerLabel(ExpenseReportDto report, bool actorIsFinanceAdmin) =>
        actorIsFinanceAdmin ? "Finance admin"
        : report.PreapprovalGrantId is null ? "Coordinator"
        : "Pre-approval manager";

    /// <summary>
    /// A pre-approved report has no category; its first reviewer is a manager of the grant's program, the person who
    /// already decided it.
    /// </summary>
    private async Task<ExpenseMutationResult?> GetFirstReviewerRefusalAsync(
        ExpenseReportDto report, Guid actorUserId, CancellationToken ct)
    {
        if (report.PreapprovalGrantId is { } grantId)
            return (await preapprovals.ListManagedGrantIdsAsync(actorUserId, ct)).Contains(grantId)
                ? null
                : ExpenseMutationResult.OperatorFailure("Actor is not a manager of the report's pre-approval program.");

        var categoryId = report.BudgetCategoryId;
        if (categoryId is null)
            return ExpenseMutationResult.OperatorFailure("Report is not booked to a budget category; there is no coordinator to endorse it.");
        var category = await budgetService.GetCategoryByIdAsync(categoryId.Value);
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

    /// <summary>Includes accounts merged into this one. The member's IBAN is Finance's slice.</summary>
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
        };

        var auditEntries = (await auditLogService.GetFilteredEntriesAsync(
            userId: userId,
            actions: expenseActions,
            limit: 10_000,
            ct: ct)).ToList();

        // Decisions others took on the member's claims name the claim, not the member.
        foreach (var report in allReports)
        {
            auditEntries.AddRange(await auditLogService.GetFilteredEntriesAsync(
                entityType: AuditEntityTypes.Report,
                entityId: report.Id,
                actions: expenseActions,
                limit: 10_000,
                ct: ct));
        }
        auditEntries = auditEntries
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
                    ? new { Entries = shapedAudit }
                    : (object?)null),
        ];
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
}
