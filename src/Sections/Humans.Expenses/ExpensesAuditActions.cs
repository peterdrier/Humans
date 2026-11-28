using Humans.Audit.Contracts;

namespace Humans.Expenses;

/// <summary>The audit actions Expenses writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class ExpensesAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition ExpenseSubmit = new("ExpenseSubmit", AuditKept.Forever);

    public static readonly AuditActionDefinition ExpenseEndorse = new("ExpenseEndorse", AuditKept.Forever);

    public static readonly AuditActionDefinition ExpenseCoordinatorReject = new("ExpenseCoordinatorReject", AuditKept.Forever);

    public static readonly AuditActionDefinition ExpenseApprove = new("ExpenseApprove", AuditKept.Forever);

    public static readonly AuditActionDefinition ExpenseReject = new("ExpenseReject", AuditKept.Forever);

    public static readonly AuditActionDefinition ExpenseWithdraw = new("ExpenseWithdraw", AuditKept.Forever);

    public static readonly AuditActionDefinition ExpenseCategoryOverride = new("ExpenseCategoryOverride", AuditKept.Forever);

    public static readonly AuditActionDefinition ExpenseSepaSent = new("ExpenseSepaSent", AuditKept.Forever);

    public static readonly AuditActionDefinition ExpenseSepaReopened = new("ExpenseSepaReopened", AuditKept.Forever);

    public static readonly AuditActionDefinition ExpensePaid = new("ExpensePaid", AuditKept.Forever);

    public static readonly AuditActionDefinition IbanSet = new("IbanSet", AuditKept.Forever);

    public static readonly AuditActionDefinition IbanRemove = new("IbanRemove", AuditKept.Forever);

    public static readonly AuditActionDefinition ExpenseAttachmentUploaded = new("ExpenseAttachmentUploaded", AuditKept.Forever);

    public static readonly AuditActionDefinition ExpenseAttachmentRemoved = new("ExpenseAttachmentRemoved", AuditKept.Forever);

    // Written by the Holded outbox drain, so a report's push history survives outbox-row cleanup.
    public static readonly AuditActionDefinition ExpenseHoldedPushed = new("ExpenseHoldedPushed", AuditKept.Forever);

    public static readonly AuditActionDefinition ExpenseHoldedFailed = new("ExpenseHoldedFailed", AuditKept.Forever);

    public static readonly AuditActionDefinition ExpenseHoldedRequeued = new("ExpenseHoldedRequeued", AuditKept.Forever);

    // Only the on-behalf case is audited, never self-created drafts.
    public static readonly AuditActionDefinition ExpenseCreatedOnBehalf = new("ExpenseCreatedOnBehalf", AuditKept.Forever);

    // Any change to another member's report; the description says what. Self-edits are not audited.
    public static readonly AuditActionDefinition ExpenseEditedOnBehalf = new("ExpenseEditedOnBehalf", AuditKept.Forever);

    // Entity is the report, so it shows in that report's history. The IBAN is unmasked when somebody set it
    // for another member.
    public static readonly AuditActionDefinition ExpensePayeeIbanUpdated = new("ExpensePayeeIbanUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition ExpenseLineDeclined = new("ExpenseLineDeclined", AuditKept.Forever);

    public static readonly AuditActionDefinition ExpenseLineDeclineUndone = new("ExpenseLineDeclineUndone", AuditKept.Forever);
}
