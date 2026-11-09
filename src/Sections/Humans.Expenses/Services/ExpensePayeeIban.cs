using Humans.AuditLog.Contracts;
using Humans.Base.Helpers;
using Humans.Base.Interfaces;
using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
using Humans.Expenses.Domain;
using Humans.Expenses.Services.Dtos;
using Humans.Finance.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Expenses.Services;

internal sealed class ExpensePayeeIban(
    IExpenseRepository repo,
    IUserService userService,
    IFinanceWrite financeWrite,
    IAuditLogService auditLogService,
    IClock clock,
    ILogger<ExpenseReportService> logger,
    ExpenseReportAudit reportAudit) : IApplicationService
{
    internal async Task<ExpenseIbanSaveResult> SaveSubmitterIbanWithResultAsync(
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
        var snapshotIsLive = ExpenseReportAccess.IsPendingApproval(report.Status);
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
        var description = actorUserId == report.SubmitterUserId
            ? "Payee IBAN updated"
            : $"Payee IBAN updated for {await reportAudit.DescribeMemberAsync(report.SubmitterUserId, ct)} to {normalizedIban}";

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
        if (actorUserId == submitterUserId)
            return normalizedIban is null ? "IBAN removed" : "IBAN set";

        var member = await reportAudit.DescribeMemberAsync(submitterUserId, ct);
        return normalizedIban is null
            ? $"IBAN removed for {member}"
            : $"IBAN set for {member} to {normalizedIban}";
    }

}
