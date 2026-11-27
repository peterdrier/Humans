using Humans.Audit.Contracts;

namespace Humans.Finance.Contracts;

/// <summary>The audit actions Finance writes. Names are stored on audit rows: never rename one.</summary>
public sealed class FinanceAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition IbanReveal = new("IbanReveal", AuditKept.Forever);

    // Outside the budget map, on a caller's behalf.
    public static readonly AuditActionDefinition HoldedExpenseAccountCreated = new("HoldedExpenseAccountCreated", AuditKept.Forever);

    public static readonly AuditActionDefinition HoldedExpenseAccountLinked = new("HoldedExpenseAccountLinked", AuditKept.Forever);

    // A managed account retired or restored (a workgroup lifecycle hook).
    public static readonly AuditActionDefinition HoldedExpenseAccountActiveChanged = new("HoldedExpenseAccountActiveChanged", AuditKept.Forever);

    // One per credit transfer in a payout file. The IBAN in the description is always masked.
    public static readonly AuditActionDefinition SepaPayoutTransfer = new("SepaPayoutTransfer", AuditKept.Forever);

    // The IBAN in the description is always masked.
    public static readonly AuditActionDefinition SepaPayoutTransferBooked = new("SepaPayoutTransferBooked", AuditKept.Forever);

    // Written once, when the sweep starts booking past a Sabadell line left pending with no readable date for
    // several runs.
    public static readonly AuditActionDefinition SepaBankLineSkipped = new("SepaBankLineSkipped", AuditKept.Forever);

    // The bank cancelled the transfer before execution. The row stays, flagged withdrawn, and no matcher pairs it again.
    public static readonly AuditActionDefinition SepaPayoutTransferWithdrawn = new("SepaPayoutTransferWithdrawn", AuditKept.Forever);

    // A Holded purchase document or contact created twice by the expense push. Names the member, the amount and
    // the one kept.
    public static readonly AuditActionDefinition HoldedDuplicateRemoved = new("HoldedDuplicateRemoved", AuditKept.Forever);

    public static readonly AuditActionDefinition HoldedCreditorBound = new("HoldedCreditorBound", AuditKept.Forever);

    public static readonly AuditActionDefinition HoldedCreditorUnbound = new("HoldedCreditorUnbound", AuditKept.Forever);

    // Holded assigned or reassigned the 400000xx on a creditor binding. Written under the pushing job's name.
    public static readonly AuditActionDefinition HoldedCreditorAccountNumChanged = new("HoldedCreditorAccountNumChanged", AuditKept.Forever);

    public static readonly AuditActionDefinition HoldedCategoryAccountProvisioned = new("HoldedCategoryAccountProvisioned", AuditKept.Forever);

    // One per Pleo entry booked (or possibly booked) into Holded as its Pleo-{Id} purchase, or finished: names the
    // entry, the account, the amount, the receipts attached and the outcome.
    public static readonly AuditActionDefinition PleoEntryBooked = new("PleoEntryBooked", AuditKept.Forever);

    // A Pleo receipt that arrived after its entry was booked, attached to the entry's Holded purchase.
    public static readonly AuditActionDefinition PleoReceiptAttached = new("PleoReceiptAttached", AuditKept.Forever);

    // A correcting journal entry for a booked Pleo entry recategorised or re-priced in Pleo since.
    public static readonly AuditActionDefinition PleoCorrectionPosted = new("PleoCorrectionPosted", AuditKept.Forever);

    // The Pleo booking settings (Holded contact and treasury) were changed on /Settings#finance.
    public static readonly AuditActionDefinition FinancePleoSettingsUpdated = new("FinancePleoSettingsUpdated", AuditKept.Forever);

    // The Stripe account each 441 ledger account is compared with on /Finance was changed on /Settings#finance.
    public static readonly AuditActionDefinition FinanceStripeLedgerLinksUpdated = new("FinanceStripeLedgerLinksUpdated", AuditKept.Forever);
}
