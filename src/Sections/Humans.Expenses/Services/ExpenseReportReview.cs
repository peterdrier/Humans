using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
using Humans.Expenses.Services.Dtos;
using NodaTime;

namespace Humans.Expenses.Services;

internal sealed class ExpenseReportReview(
    IExpenseRepository repo,
    IAuditLogService auditLogService,
    IClock clock,
    ExpenseReportAccess reportAccess) : IApplicationService
{
    private const int DeclineReasonMaxLength = 1000;
    private static readonly ExpenseReportStatus[] FirstReviewStatuses = [ExpenseReportStatus.Submitted];
    private static readonly ExpenseReportStatus[] FinanceReviewStatuses =
        [ExpenseReportStatus.Submitted, ExpenseReportStatus.CoordinatorEndorsed];

    internal async Task<ExpenseMutationResult> CoordinatorEndorseWithResultAsync(
        Guid reportId, Guid coordinatorUserId, decimal? maxAmount,
        CancellationToken ct = default, string? auditReviewerLabel = null)
    {
        var report = await repo.GetByIdAsync(reportId, ct);
        if (report is null) return ExpenseMutationResult.OperatorFailure("Could not endorse the report. It may no longer be in Submitted status.");

        var cap = await reportAccess.ResolveReviewCapAsync(report.PreapprovalGrantId, maxAmount, ct);
        if (cap.Refusal is { } capRefusal) return capRefusal;
        maxAmount = cap.Amount;

        var now = clock.GetCurrentInstant();
        var ok = await repo.CoordinatorEndorseAsync(reportId, coordinatorUserId, maxAmount, now, ct);
        if (!ok) return ExpenseMutationResult.OperatorFailure("Could not endorse the report. It may no longer be in Submitted status.");

        await auditLogService.LogAsync(
            ExpensesAuditActions.ExpenseEndorse,
            AuditEntityTypes.Report, reportId,
            (auditReviewerLabel ?? ReviewerLabel(report))
                + " endorsed expense report." + ExpenseReportAudit.MaxAmountDetail(maxAmount),
            coordinatorUserId);

        return ExpenseMutationResult.Success;

    }

    internal async Task<ExpenseMutationResult> CoordinatorRejectWithResultAsync(
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
            AuditEntityTypes.Report, reportId,
            $"{(auditReviewerLabel ?? ReviewerLabel(report))} rejected expense report: {reason}",
            coordinatorUserId);

        return ExpenseMutationResult.Success;

    }

    internal Task<ExpenseMutationResult> DeclineLineWithResultAsync(
        Guid reportId, Guid actorUserId, Guid lineId, string reason, CancellationToken ct = default) =>
        DeclineLineAsync(reportId, actorUserId, lineId, reason, FirstReviewStatuses, null, ct);

    internal Task<ExpenseMutationResult> DeclineLineForFinanceReviewWithResultAsync(
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

        var (report, line, refusal) = await reportAccess.GetDeclinableLineAsync(
            reportId, lineId, allowedStatuses, ct);
        if (refusal is not null) return refusal;
        if (line!.Declined)
            return ExpenseMutationResult.OperatorFailure("That line is already declined.");

        var ok = await repo.SetLineDeclinedAsync(
            reportId, lineId, declined: true, trimmed, actorUserId, clock.GetCurrentInstant(), ct);
        if (!ok) return ExpenseMutationResult.OperatorFailure("Could not decline the line.");

        await auditLogService.LogAsync(
            ExpensesAuditActions.ExpenseLineDeclined,
            AuditEntityTypes.Report, reportId,
            $"{(auditReviewerLabel ?? ReviewerLabel(report!))} declined line "
                + $"\"{line.Description}\" ({line.Amount.ToString("0.00", CultureInfo.InvariantCulture)} EUR): {trimmed}",
            actorUserId);

        return ExpenseMutationResult.Success;
    }

    internal Task<ExpenseMutationResult> UndoDeclineLineWithResultAsync(
        Guid reportId, Guid actorUserId, Guid lineId, CancellationToken ct = default) =>
        UndoDeclineLineAsync(reportId, actorUserId, lineId, FirstReviewStatuses, null, ct);

    internal Task<ExpenseMutationResult> UndoDeclineLineForFinanceReviewWithResultAsync(
        Guid reportId, Guid actorUserId, Guid lineId, CancellationToken ct = default) =>
        UndoDeclineLineAsync(reportId, actorUserId, lineId, FinanceReviewStatuses, "Finance admin", ct);

    private async Task<ExpenseMutationResult> UndoDeclineLineAsync(
        Guid reportId, Guid actorUserId, Guid lineId,
        IReadOnlyCollection<ExpenseReportStatus> allowedStatuses, string? auditReviewerLabel, CancellationToken ct)
    {
        var (report, line, refusal) = await reportAccess.GetDeclinableLineAsync(
            reportId, lineId, allowedStatuses, ct);
        if (refusal is not null) return refusal;
        if (!line!.Declined)
            return ExpenseMutationResult.OperatorFailure("That line is not declined.");

        var ok = await repo.SetLineDeclinedAsync(
            reportId, lineId, declined: false, reason: null, byUserId: null, clock.GetCurrentInstant(), ct);
        if (!ok) return ExpenseMutationResult.OperatorFailure("Could not undo the decline.");

        await auditLogService.LogAsync(
            ExpensesAuditActions.ExpenseLineDeclineUndone,
            AuditEntityTypes.Report, reportId,
            $"{(auditReviewerLabel ?? ReviewerLabel(report!))} undid the decline of line "
                + $"\"{line.Description}\" ({line.Amount.ToString("0.00", CultureInfo.InvariantCulture)} EUR); "
                + $"it had been declined: {line.DeclinedReason}",
            actorUserId);

        return ExpenseMutationResult.Success;
    }

    internal async Task<ExpenseMutationResult> FinanceRejectWithResultAsync(
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
            AuditEntityTypes.Report, reportId,
            $"Finance rejected expense report: {reason}",
            actorUserId);

        return ExpenseMutationResult.Success;

    }

    private static string ReviewerLabel(ExpenseReportDto report) =>
        report.PreapprovalGrantId is null ? "Coordinator"
        : "Pre-approval manager";

}
