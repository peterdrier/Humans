using Humans.Base.Authorization;
using Humans.Permissions.Contracts;
using Humans.Base.Extensions;
using Humans.Audit.Contracts;
using Humans.Budget.Contracts;
using Humans.Email.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Teams.Contracts;
using Humans.Base.Helpers;
using NodaTime;
using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
using Humans.Users.Contracts;
using System.Globalization;
using System.Transactions;

namespace Humans.Expenses.Services;

/// <summary>Coordinator and finance decisions on submitted expense reports.</summary>
internal sealed class ExpenseReviewService(
    IExpenseRepository repo,
    IExpenseReportManagement reports,
    IBudgetService budgetService,
    IPermissionService permissions,
    IUserService userService,
    IEmailService emailService,
    ExpensesEmails emails,
    IAuditLogService auditLogService,
    IPreapprovalService preapprovals,
    IPreapprovalWrite preapprovalWrites,
    IClock clock,
    ILogger<ExpenseReviewService> logger) : IExpenseReview
{
    public Task<ExpenseMutationResult> CoordinatorEndorseWithResultAsync(
        Guid reportId, Guid coordinatorUserId, decimal? maxAmount,
        CancellationToken ct = default, string? auditReviewerLabel = null) =>
        ExpenseReportService.RunMutationAsync(logger, ct, () => CoordinatorEndorseWithResultCoreAsync(reportId, coordinatorUserId, maxAmount, ct, auditReviewerLabel),
            "Error endorsing expense report {ReportId}", reportId);

    public Task<ExpenseMutationResult> CoordinatorRejectWithResultAsync(
        Guid reportId, Guid coordinatorUserId, string reason,
        CancellationToken ct = default, string? auditReviewerLabel = null) =>
        ExpenseReportService.RunMutationAsync(logger, ct, () => CoordinatorRejectWithResultCoreAsync(reportId, coordinatorUserId, reason, ct, auditReviewerLabel),
            "Error coordinator-rejecting expense report {ReportId}", reportId);

    public Task<ExpenseMutationResult> DeclineLineWithResultAsync(
        Guid reportId, Guid actorUserId, Guid lineId, string reason,
        CancellationToken ct = default) =>
        ExpenseReportService.RunMutationAsync(logger, ct, () => DeclineLineWithResultCoreAsync(reportId, actorUserId, lineId, reason, ct),
            "Error declining line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> DeclineLineForFinanceReviewWithResultAsync(
        Guid reportId, Guid actorUserId, Guid lineId, string reason,
        CancellationToken ct = default) =>
        ExpenseReportService.RunMutationAsync(logger, ct, () => DeclineLineForFinanceReviewWithResultCoreAsync(reportId, actorUserId, lineId, reason, ct),
            "Error declining line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> UndoDeclineLineWithResultAsync(
        Guid reportId, Guid actorUserId, Guid lineId,
        CancellationToken ct = default) =>
        ExpenseReportService.RunMutationAsync(logger, ct, () => UndoDeclineLineWithResultCoreAsync(reportId, actorUserId, lineId, ct),
            "Error undoing decline of line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> UndoDeclineLineForFinanceReviewWithResultAsync(
        Guid reportId, Guid actorUserId, Guid lineId,
        CancellationToken ct = default) =>
        ExpenseReportService.RunMutationAsync(logger, ct, () => UndoDeclineLineForFinanceReviewWithResultCoreAsync(reportId, actorUserId, lineId, ct),
            "Error undoing decline of line {LineId} on report {ReportId}", lineId, reportId);

    public Task<ExpenseMutationResult> ApproveWithResultAsync(
        Guid reportId, Guid actorUserId, int? overrideAccountNumber, decimal? maxAmount,
        CancellationToken ct = default) =>
        ExpenseReportService.RunMutationAsync(logger, ct, () => ApproveWithResultCoreAsync(reportId, actorUserId, overrideAccountNumber, maxAmount, ct),
            "Error approving expense report {ReportId}", reportId);

    public Task<ExpenseMutationResult> FinanceRejectWithResultAsync(
        Guid reportId, Guid actorUserId, string reason,
        CancellationToken ct = default) =>
        ExpenseReportService.RunMutationAsync(logger, ct, () => FinanceRejectWithResultCoreAsync(reportId, actorUserId, reason, ct),
            "Error finance-rejecting expense report {ReportId}", reportId);

    public Task<IReadOnlyList<Guid>> GetFailedHoldedPushReportIdsAsync(CancellationToken ct = default)
        => repo.GetFailedOutboxReportIdsAsync(ct);

    public Task<ExpenseMutationResult> RequeueHoldedPushWithResultAsync(
        Guid reportId, Guid actorUserId, CancellationToken ct = default) =>
        ExpenseReportService.RunMutationAsync(logger, ct, async () =>
        {
            var requeued = await repo.RequeueOutboxForReportAsync(reportId, ct);
            if (!requeued) return ExpenseMutationResult.OperatorFailure(
                        "This report has no failed or retrying Holded push to re-queue.");

            await auditLogService.LogAsync(
                ExpensesAuditActions.ExpenseHoldedRequeued,
                ExpensesAuditSubjectTypes.Report, reportId,
                "Finance re-queued the Holded push.",
                actorUserId);

            return ExpenseMutationResult.Success;
        }, "Error re-queuing Holded push for expense report {ReportId}", reportId);

    public async Task<IReadOnlyList<ExpenseReportDto>> GetCoordinatorQueueAsync(
        Guid coordinatorUserId, CancellationToken ct = default)
    {
        var coordinator = await userService.GetUserInfoAsync(coordinatorUserId, ct);
        if (coordinator is null) return [];
        var categoryIds = await GetCoordinatorCategoryIdsAsync(coordinator.Id, ct);
        var managedGrantIds = await PreapprovalGrantQueries.ListManagedGrantIdsAsync(preapprovals, coordinator.AllUserIds, ct);

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
            if (await permissions.HasAsync(coordinatorUserId, ExpensesPermissions.Endorse, new Owner(TeamOwner.Kind, teamId), ct))
                coordinatedTeamIds.Add(teamId);
        }

        return categories
            .Where(c => coordinatedTeamIds.Contains(c.TeamId!.Value))
            .Select(c => c.Id)
            .ToList();
    }

    /// <summary>
    /// Checks the command's decision window and requires a top-level line. The caller authorizes the reviewer.
    /// </summary>
    internal async Task<(ExpenseReportDto? Report, ExpenseLineDto? Line, ExpenseMutationResult? Refusal)>
        GetDeclinableLineAsync(
            Guid reportId, Guid lineId, IReadOnlyCollection<ExpenseReportStatus> allowedStatuses, CancellationToken ct)
    {
        const string cannot = "Could not change the line. The report may no longer be awaiting a decision.";
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return (null, null, ExpenseMutationResult.OperatorFailure(cannot));

        if (!allowedStatuses.Contains(report.Status))
            return (null, null, ExpenseMutationResult.OperatorFailure(cannot));

        var line = report.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null) return (null, null, ExpenseMutationResult.Failure("Expenses_Validation_LineNotOnReport"));
        if (line.ParentLineId is not null)
            return (null, null, ExpenseMutationResult.OperatorFailure("Proof rows cannot be declined; decline the invoice line instead."));
        return (report, line, null);
    }

    internal async Task<(decimal? Amount, ExpenseMutationResult? Refusal)> ResolveReviewCapAsync(
        Guid? grantId, decimal? requestedAmount, CancellationToken ct)
    {
        if (requestedAmount is { } amount && decimal.Round(amount, 2) != amount)
            return (null, ExpenseMutationResult.Failure("Expenses_Validation_AmountPrecision"));

        if (grantId is not { } id) return (requestedAmount, null);

        var grant = await PreapprovalGrantQueries.GetGrantAsync(preapprovals, id, ct);
        if (grant is null)
            return (null, ExpenseMutationResult.OperatorFailure("The report's pre-approval no longer exists; reject the report."));
        if (requestedAmount > grant.MaxAmount)
            return (null, ExpenseMutationResult.OperatorFailure(
                $"The cap cannot exceed the pre-approved {grant.MaxAmount.ToString("0.00", CultureInfo.InvariantCulture)} EUR."));

        return (requestedAmount ?? grant.MaxAmount, null);
    }

    private const int DeclineReasonMaxLength = 1000;
    private static readonly ExpenseReportStatus[] FirstReviewStatuses = [ExpenseReportStatus.Submitted];
    private static readonly ExpenseReportStatus[] FinanceReviewStatuses =
        [ExpenseReportStatus.Submitted, ExpenseReportStatus.CoordinatorEndorsed];

    internal async Task<ExpenseMutationResult> CoordinatorEndorseWithResultCoreAsync(
        Guid reportId, Guid coordinatorUserId, decimal? maxAmount,
        CancellationToken ct = default, string? auditReviewerLabel = null)
    {
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return ExpenseMutationResult.OperatorFailure("Could not endorse the report. It may no longer be in Submitted status.");

        var cap = await ResolveReviewCapAsync(report.PreapprovalGrantId, maxAmount, ct);
        if (cap.Refusal is { } capRefusal) return capRefusal;
        maxAmount = cap.Amount;

        var now = clock.GetCurrentInstant();
        var ok = await repo.CoordinatorEndorseAsync(reportId, coordinatorUserId, maxAmount, now, ct);
        if (!ok) return ExpenseMutationResult.OperatorFailure("Could not endorse the report. It may no longer be in Submitted status.");

        await auditLogService.LogAsync(
            ExpensesAuditActions.ExpenseEndorse,
            ExpensesAuditSubjectTypes.Report, reportId,
            (auditReviewerLabel ?? ReviewerLabel(report))
                + " endorsed expense report." + ExpenseReportService.MaxAmountDetail(maxAmount),
            coordinatorUserId);

        return ExpenseMutationResult.Success;
    }

    internal async Task<ExpenseMutationResult> CoordinatorRejectWithResultCoreAsync(
        Guid reportId, Guid coordinatorUserId, string reason,
        CancellationToken ct = default, string? auditReviewerLabel = null)
    {
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return ExpenseMutationResult.OperatorFailure("Could not reject the report. It may no longer be in Submitted status.");

        var now = clock.GetCurrentInstant();
        var ok = await repo.CoordinatorRejectAsync(reportId, coordinatorUserId, reason, now, ct);
        if (!ok) return ExpenseMutationResult.OperatorFailure("Could not reject the report. It may no longer be in Submitted status.");

        await auditLogService.LogAsync(
            ExpensesAuditActions.ExpenseCoordinatorReject,
            ExpensesAuditSubjectTypes.Report, reportId,
            $"{(auditReviewerLabel ?? ReviewerLabel(report))} rejected expense report: {reason}",
            coordinatorUserId);

        return ExpenseMutationResult.Success;
    }

    internal Task<ExpenseMutationResult> DeclineLineWithResultCoreAsync(
        Guid reportId, Guid actorUserId, Guid lineId, string reason, CancellationToken ct = default) =>
        DeclineLineAsync(reportId, actorUserId, lineId, reason, FirstReviewStatuses, null, ct);

    internal Task<ExpenseMutationResult> DeclineLineForFinanceReviewWithResultCoreAsync(
        Guid reportId, Guid actorUserId, Guid lineId, string reason, CancellationToken ct = default) =>
        DeclineLineAsync(reportId, actorUserId, lineId, reason, FinanceReviewStatuses, "Finance admin", ct);

    private async Task<ExpenseMutationResult> DeclineLineAsync(
        Guid reportId, Guid actorUserId, Guid lineId, string reason,
        IReadOnlyCollection<ExpenseReportStatus> allowedStatuses, string? auditReviewerLabel, CancellationToken ct)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return ExpenseMutationResult.OperatorFailure("A reason is required to decline a line.");
        if (trimmed.Length > DeclineReasonMaxLength)
            return ExpenseMutationResult.OperatorFailure(
                $"The reason can be at most {DeclineReasonMaxLength} characters.");

        var (report, line, refusal) = await GetDeclinableLineAsync(
            reportId, lineId, allowedStatuses, ct);
        if (refusal is not null) return refusal;
        if (line!.Declined)
            return ExpenseMutationResult.OperatorFailure("That line is already declined.");

        var ok = await repo.SetLineDeclinedAsync(
            reportId, lineId, declined: true, trimmed, actorUserId, clock.GetCurrentInstant(), ct);
        if (!ok) return ExpenseMutationResult.OperatorFailure("Could not decline the line.");

        await auditLogService.LogAsync(
            ExpensesAuditActions.ExpenseLineDeclined,
            ExpensesAuditSubjectTypes.Report, reportId,
            $"{(auditReviewerLabel ?? ReviewerLabel(report!))} declined line "
                + $"\"{line.Description}\" ({line.Amount.ToString("0.00", CultureInfo.InvariantCulture)} EUR): {trimmed}",
            actorUserId);

        return ExpenseMutationResult.Success;
    }

    internal Task<ExpenseMutationResult> UndoDeclineLineWithResultCoreAsync(
        Guid reportId, Guid actorUserId, Guid lineId, CancellationToken ct = default) =>
        UndoDeclineLineAsync(reportId, actorUserId, lineId, FirstReviewStatuses, null, ct);

    internal Task<ExpenseMutationResult> UndoDeclineLineForFinanceReviewWithResultCoreAsync(
        Guid reportId, Guid actorUserId, Guid lineId, CancellationToken ct = default) =>
        UndoDeclineLineAsync(reportId, actorUserId, lineId, FinanceReviewStatuses, "Finance admin", ct);

    private async Task<ExpenseMutationResult> UndoDeclineLineAsync(
        Guid reportId, Guid actorUserId, Guid lineId,
        IReadOnlyCollection<ExpenseReportStatus> allowedStatuses, string? auditReviewerLabel, CancellationToken ct)
    {
        var (report, line, refusal) = await GetDeclinableLineAsync(
            reportId, lineId, allowedStatuses, ct);
        if (refusal is not null) return refusal;
        if (!line!.Declined)
            return ExpenseMutationResult.OperatorFailure("That line is not declined.");

        var ok = await repo.SetLineDeclinedAsync(
            reportId, lineId, declined: false, reason: null, byUserId: null, clock.GetCurrentInstant(), ct);
        if (!ok) return ExpenseMutationResult.OperatorFailure("Could not undo the decline.");

        await auditLogService.LogAsync(
            ExpensesAuditActions.ExpenseLineDeclineUndone,
            ExpensesAuditSubjectTypes.Report, reportId,
            $"{(auditReviewerLabel ?? ReviewerLabel(report!))} undid the decline of line "
                + $"\"{line.Description}\" ({line.Amount.ToString("0.00", CultureInfo.InvariantCulture)} EUR); "
                + $"it had been declined: {line.DeclinedReason}",
            actorUserId);

        return ExpenseMutationResult.Success;
    }

    internal async Task<ExpenseMutationResult> FinanceRejectWithResultCoreAsync(
        Guid reportId, Guid actorUserId, string reason,
        CancellationToken ct = default)
    {
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return ExpenseMutationResult.OperatorFailure("Could not reject the report. It may not be in a rejectable status.");

        var now = clock.GetCurrentInstant();
        var ok = await repo.FinanceRejectAsync(reportId, actorUserId, reason, now, ct);
        if (!ok) return ExpenseMutationResult.OperatorFailure("Could not reject the report. It may not be in a rejectable status.");

        await auditLogService.LogAsync(
            ExpensesAuditActions.ExpenseReject,
            ExpensesAuditSubjectTypes.Report, reportId,
            $"Finance rejected expense report: {reason}",
            actorUserId);

        return ExpenseMutationResult.Success;
    }

    private static string ReviewerLabel(ExpenseReportDto report) =>
        report.PreapprovalGrantId is null ? "Coordinator"
        : "Pre-approval manager";

    internal async Task<ExpenseMutationResult> ApproveWithResultCoreAsync(
        Guid reportId, Guid actorUserId, int? overrideAccountNumber, decimal? maxAmount,
        CancellationToken ct = default)
    {
        using var transition = await ExpenseReportService.TransitionLock.AcquireAsync(logger, ct);
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return ExpenseMutationResult.OperatorFailure("Could not approve the report. It may not be in an approvable status.");

        // Re-booking at approval keeps the report's year: a category account from another year
        // is refused the same way a header edit would refuse it.
        ExpenseBooking? overrideBooking = null;
        if (overrideAccountNumber is { } overrideNumber && overrideNumber != report.HoldedAccountNumber)
        {
            if (report.PreapprovalGrantId is not null)
                return ExpenseMutationResult.OperatorFailure("The account is locked by the report's pre-approval; reject the report to change it.");
            var (resolved, refusal) = await reports.ResolveBookingAsync(overrideNumber, report.BudgetYearId, ct);
            if (refusal is not null) return refusal;
            overrideBooking = resolved;
        }

        var cap = await ResolveReviewCapAsync(report.PreapprovalGrantId, maxAmount, ct);
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
                ExpensesAuditSubjectTypes.Report, reportId,
                "Finance approved expense report." + ExpenseReportService.MaxAmountDetail(maxAmount)
                + (report.PreapprovalGrantId is { } g ? $" Consumed pre-approval {g}." : ""),
                actorUserId);

            if (overrideBooking is not null)
            {
                await auditLogService.LogAsync(
                    ExpensesAuditActions.ExpenseCategoryOverride,
                    ExpensesAuditSubjectTypes.Report, reportId,
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
        if (submitter is null || submitter.IsTombstone || string.IsNullOrWhiteSpace(recipient))
        {
            logger.LogWarning(
                "Skipping expense-approved email for report {ReportId}: submitter {UserId} is unavailable or has no notification email",
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
