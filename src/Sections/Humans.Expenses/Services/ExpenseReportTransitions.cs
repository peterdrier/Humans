using System.Transactions;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Base.Helpers;
using Humans.Base.Interfaces;
using Humans.Base.Threading;
using Humans.Email.Contracts;
using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
using Humans.Expenses.Domain;
using Humans.Expenses.Services.Dtos;
using Humans.Finance.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Expenses.Services;

internal sealed class ExpenseReportTransitions(
    IExpenseRepository repo,
    IUserService userService,
    IEmailService emailService,
    ExpensesEmails emails,
    IAuditLogService auditLogService,
    IFinanceService finance,
    IPreapprovalService preapprovals,
    IPreapprovalWrite preapprovalWrites,
    IClock clock,
    ILogger<ExpenseReportService> logger,
    ExpenseReportAccess reportAccess,
    ExpenseReportAudit reportAudit) : IApplicationService
{
    // Scoped transitions hold this through ambient transaction disposal and completion.
    private static readonly TrackedLock TransitionLock = new("Expenses.ReportTransitions");

    internal async Task<ExpenseMutationResult> UpdateDraftWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin,
        int holdedAccountNumber, string? note,
        Guid? preapprovalGrantId = null,
        CancellationToken ct = default)
    {
        using var transition = await TransitionLock.AcquireAsync(logger, ct);
        var (editableReport, refusal) = await reportAccess.GetEditableReportAsync(reportId, actorUserId, actorIsFinanceAdmin, ct);
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
            var (grantBooking, grantRefusal) = await reportAccess.ResolveGrantBookingAsync(
                grantId, report.SubmitterUserId, report.PreapprovalGrantId, ct);
            if (grantRefusal is not null) return grantRefusal;
            booking = grantBooking!;
        }
        else
        {
            // A draft resolves through the active year. A submitted report already belongs to a year;
            // re-resolving it would silently move last year's accounting into this year's books.
            var pinnedYearId = ExpenseReportAccess.IsPendingApproval(report.Status) ? report.BudgetYearId : null;
            var (resolved, bookingRefusal) = await reportAccess.ResolveBookingAsync(holdedAccountNumber, pinnedYearId, ct);
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

        await reportAudit.AuditOnBehalfEditAsync(report, actorUserId,
            $"Updated header (account {booking.HoldedAccountNumber}, subject {ExpenseReportAudit.DescribeNote(note)})"
            + (preapprovalGrantId != report.PreapprovalGrantId
                ? $" and pre-approval ({report.PreapprovalGrantId?.ToString() ?? "none"} → {preapprovalGrantId?.ToString() ?? "none"})"
                : ""), ct);

        return ExpenseMutationResult.Success;
    }

    internal async Task<ExpenseMutationResult> SubmitWithResultAsync(
        Guid reportId, Guid actorUserId, bool actorIsFinanceAdmin, CancellationToken ct = default)
    {
        using var transition = await TransitionLock.AcquireAsync(logger, ct);
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return ExpenseMutationResult.Failure("Expenses_Validation_CouldNotSubmitReport");
        if (!actorIsFinanceAdmin && !await reportAccess.IsSubmitterAsync(report, actorUserId, ct))
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
            (report.SubmitterUserId == actorUserId
                ? "Submitted expense report."
                : $"Submitted expense report on behalf of {await reportAudit.DescribeMemberAsync(report.SubmitterUserId, ct)}.")
            + ExpenseReportAudit.DescribeGrant(report.PreapprovalGrantId),
            actorUserId,
            relatedEntityId: report.SubmitterUserId,
            relatedEntityType: AuditSubjectTypes.User);

        return ExpenseMutationResult.Success;

    }

    internal async Task<ExpenseMutationResult> WithdrawWithResultAsync(
        Guid reportId, Guid submitterUserId, CancellationToken ct = default)
    {
        using var transition = await TransitionLock.AcquireAsync(logger, ct);
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return ExpenseMutationResult.Failure("Expenses_Flash_WithdrawFailed");
        if (!await reportAccess.IsSubmitterAsync(report, submitterUserId, ct))
            return ExpenseMutationResult.Failure("Expenses_Validation_OnlySubmitterCanWithdraw");

        // Approval queues the Holded push, so only a report still pending approval can be
        // withdrawn; the repository refuses anything else too.
        if (!ExpenseReportAccess.IsPendingApproval(report.Status))
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

    internal async Task<ExpenseMutationResult> ApproveWithResultAsync(
        Guid reportId, Guid actorUserId, int? overrideAccountNumber, decimal? maxAmount,
        CancellationToken ct = default)
    {
        using var transition = await TransitionLock.AcquireAsync(logger, ct);
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return ExpenseMutationResult.OperatorFailure("Could not approve the report. It may not be in an approvable status.");

        // Re-booking at approval keeps the report's year: a category account from another year
        // is refused the same way a header edit would refuse it.
        ExpenseBooking? overrideBooking = null;
        if (overrideAccountNumber is { } overrideNumber && overrideNumber != report.HoldedAccountNumber)
        {
            if (report.PreapprovalGrantId is not null)
                return ExpenseMutationResult.OperatorFailure("The account is locked by the report's pre-approval; reject the report to change it.");
            var (resolved, refusal) = await reportAccess.ResolveBookingAsync(overrideNumber, report.BudgetYearId, ct);
            if (refusal is not null) return refusal;
            overrideBooking = resolved;
        }

        var cap = await reportAccess.ResolveReviewCapAsync(report.PreapprovalGrantId, maxAmount, ct);
        if (cap.Refusal is { } capRefusal) return capRefusal;
        maxAmount = cap.Amount;

        var outboxEventId = Guid.NewGuid();
        var now = clock.GetCurrentInstant();
        bool? ok = null;
        var attempted = false;
        var committed = false;
        try
        {
            if (report.PreapprovalGrantId is not { } consumedGrantId)
            {
                attempted = true;
                ok = await repo.ApproveAsync(
                    reportId, actorUserId, overrideBooking, maxAmount, now, outboxEventId, ct);
                committed = ok == true;
            }
            else
            {
                var afterCommit = new List<Func<CancellationToken, Task>>();
                using (var scope = new TransactionScope(
                    TransactionScopeOption.Required,
                    new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
                    TransactionScopeAsyncFlowOption.Enabled))
                {
                    var consumption = await preapprovalWrites.ConsumeAsync(consumedGrantId, reportId, actorUserId, ct, afterCommit.Add);
                    if (consumption.Refusal is { } refusal)
                    {
                        logger.LogWarning("Pre-approval {GrantId} refused consumption by report {ReportId}: {Reason}", consumedGrantId, reportId, refusal);
                        return ExpenseMutationResult.OperatorFailure("The report's pre-approval was revoked or is no longer held by it; reject the report.");
                    }
                    attempted = true;
                    ok = await repo.ApproveAsync(
                        reportId, actorUserId, overrideBooking, maxAmount, now, outboxEventId, ct);
                    if (ok == true) scope.Complete();
                }
                if (ok == true)
                {
                    committed = true;
                    foreach (var complete in afterCommit) await complete(CancellationToken.None);
                }
            }
            if (ok != true) return ExpenseMutationResult.OperatorFailure("Could not approve the report. It may not be in an approvable status.");

            await auditLogService.LogAsync(
                ExpensesAuditActions.ExpenseApprove,
                AuditEntityTypes.Report, reportId,
                "Finance approved expense report." + ExpenseReportAudit.MaxAmountDetail(maxAmount)
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
            return ExpenseMutationResult.Success;
        }
        finally
        {
            if (committed || (attempted && ok != false))
            {
                try
                {
                    // The report and its push event commit atomically. Match this attempt's event before
                    // announcing an approval whose save acknowledgement failed.
                    if (!committed)
                        committed = (await repo.GetLatestOutboxForReportAsync(reportId, CancellationToken.None))?.Id == outboxEventId;
                    if (committed)
                        await SendApprovedEmailAsync(reportId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Could not verify approval or send its notification for expense report {ReportId}", reportId);
                }
            }
        }

    }

    private async Task<ExpenseMutationResult?> GetGrantClaimRefusalAsync(Guid grantId, Guid reportId, CancellationToken ct)
    {
        var grant = await preapprovals.GetGrantAsync(grantId, ct);
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

    /// <summary>
    /// Re-reads the report so the amount is the DTO's own <c>Payable</c>, cap included. Sent after the save: the outbox
    /// row is a promise of money, and a failed approval must not make it.
    /// </summary>
    private async Task SendApprovedEmailAsync(Guid reportId, CancellationToken ct)
    {
        var approved = await repo.GetByIdAsync(reportId, ct);
        if (approved is null) return;

        var submitter = await userService.GetUserInfoAsync(approved.SubmitterUserId, ct);
        var recipient = submitter?.Email;
        if (submitter is null || string.IsNullOrWhiteSpace(recipient))
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

}
