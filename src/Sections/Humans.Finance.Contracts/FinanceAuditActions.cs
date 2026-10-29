using Humans.AuditLog.Contracts;

namespace Humans.Finance.Contracts;

/// <summary>The audit actions Finance writes. Names are stored on audit rows: never rename one.</summary>
public sealed class FinanceAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition IbanReveal = new("IbanReveal");

    // Outside the budget map, on a caller's behalf.
    public static readonly AuditActionDefinition HoldedExpenseAccountCreated = new("HoldedExpenseAccountCreated");

    public static readonly AuditActionDefinition HoldedExpenseAccountLinked = new("HoldedExpenseAccountLinked");

    // A managed account retired or restored (a workgroup lifecycle hook).
    public static readonly AuditActionDefinition HoldedExpenseAccountActiveChanged = new("HoldedExpenseAccountActiveChanged");

    // One per credit transfer in a payout file. The IBAN in the description is always masked.
    public static readonly AuditActionDefinition SepaPayoutTransfer = new("SepaPayoutTransfer");

    // The IBAN in the description is always masked.
    public static readonly AuditActionDefinition SepaPayoutTransferBooked = new("SepaPayoutTransferBooked");

    // Written once, when the sweep starts booking past a Sabadell line left pending with no readable date for
    // several runs.
    public static readonly AuditActionDefinition SepaBankLineSkipped = new("SepaBankLineSkipped");

    // The bank cancelled the transfer before execution. The row stays, flagged withdrawn, and no matcher pairs it again.
    public static readonly AuditActionDefinition SepaPayoutTransferWithdrawn = new("SepaPayoutTransferWithdrawn");

    // A Holded purchase document or contact created twice by the expense push. Names the member, the amount and
    // the one kept.
    public static readonly AuditActionDefinition HoldedDuplicateRemoved = new("HoldedDuplicateRemoved");

    public static readonly AuditActionDefinition HoldedCreditorBound = new("HoldedCreditorBound");

    public static readonly AuditActionDefinition HoldedCreditorUnbound = new("HoldedCreditorUnbound");

    // Holded assigned or reassigned the 400000xx on a creditor binding. Written under the pushing job's name.
    public static readonly AuditActionDefinition HoldedCreditorAccountNumChanged = new("HoldedCreditorAccountNumChanged");

    public static readonly AuditActionDefinition HoldedCategoryAccountProvisioned = new("HoldedCategoryAccountProvisioned");
}
