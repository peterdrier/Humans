using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Base.Helpers;
using Humans.Base.Interfaces;
using Humans.Expenses.Data;
using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;

namespace Humans.Expenses.Services;

internal sealed class ExpenseReportExport(
    IExpenseRepository repo,
    IUserService userService,
    IAuditViewerService auditViewer) : IApplicationService
{
    internal const string ExpenseReports = "ExpenseReports";
    internal const string ExpenseAuditLog = "ExpenseAuditLog";

    internal async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(
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
                Action = e.Action.Name,
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

}
