using Humans.AuditLog.Contracts;

namespace Humans.Expenses;

/// <summary>The audit actions Expenses writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class ExpensesAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition ExpenseSubmit = new("ExpenseSubmit");

    public static readonly AuditActionDefinition ExpenseEndorse = new("ExpenseEndorse");

    public static readonly AuditActionDefinition ExpenseCoordinatorReject = new("ExpenseCoordinatorReject");

    public static readonly AuditActionDefinition ExpenseApprove = new("ExpenseApprove");

    public static readonly AuditActionDefinition ExpenseReject = new("ExpenseReject");

    public static readonly AuditActionDefinition ExpenseWithdraw = new("ExpenseWithdraw");

    public static readonly AuditActionDefinition ExpenseCategoryOverride = new("ExpenseCategoryOverride");

    public static readonly AuditActionDefinition ExpenseSepaSent = new("ExpenseSepaSent");

    public static readonly AuditActionDefinition ExpenseSepaReopened = new("ExpenseSepaReopened");

    public static readonly AuditActionDefinition ExpensePaid = new("ExpensePaid");

    public static readonly AuditActionDefinition IbanSet = new("IbanSet");

    public static readonly AuditActionDefinition IbanRemove = new("IbanRemove");

    public static readonly AuditActionDefinition ExpenseAttachmentUploaded = new("ExpenseAttachmentUploaded");

    public static readonly AuditActionDefinition ExpenseAttachmentRemoved = new("ExpenseAttachmentRemoved");

    // Written by the Holded outbox drain, so a report's push history survives outbox-row cleanup.
    public static readonly AuditActionDefinition ExpenseHoldedPushed = new("ExpenseHoldedPushed");

    public static readonly AuditActionDefinition ExpenseHoldedFailed = new("ExpenseHoldedFailed");

    public static readonly AuditActionDefinition ExpenseHoldedRequeued = new("ExpenseHoldedRequeued");

    // Only the on-behalf case is audited, never self-created drafts.
    public static readonly AuditActionDefinition ExpenseCreatedOnBehalf = new("ExpenseCreatedOnBehalf");

    // Any change to another member's report; the description says what. Self-edits are not audited.
    public static readonly AuditActionDefinition ExpenseEditedOnBehalf = new("ExpenseEditedOnBehalf");

    // Entity is the report, so it shows in that report's history. The IBAN is unmasked when somebody set it
    // for another member.
    public static readonly AuditActionDefinition ExpensePayeeIbanUpdated = new("ExpensePayeeIbanUpdated");

    public static readonly AuditActionDefinition ExpenseLineDeclined = new("ExpenseLineDeclined");

    public static readonly AuditActionDefinition ExpenseLineDeclineUndone = new("ExpenseLineDeclineUndone");
}
