using Humans.Audit.Contracts;

namespace Humans.Store;

/// <summary>The audit actions Store writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class StoreAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition StoreOrderCreated = new("StoreOrderCreated", AuditKept.Forever);

    public static readonly AuditActionDefinition StoreOrderDeleted = new("StoreOrderDeleted", AuditKept.Forever);

    public static readonly AuditActionDefinition StoreLineAdded = new("StoreLineAdded", AuditKept.Forever);

    public static readonly AuditActionDefinition StoreLineRemoved = new("StoreLineRemoved", AuditKept.Forever);

    public static readonly AuditActionDefinition StoreCounterpartyEdited = new("StoreCounterpartyEdited", AuditKept.Forever);

    public static readonly AuditActionDefinition StoreProductCreated = new("StoreProductCreated", AuditKept.Forever);

    public static readonly AuditActionDefinition StoreProductUpdated = new("StoreProductUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition StoreProductPriceChanged = new("StoreProductPriceChanged", AuditKept.Forever);

    public static readonly AuditActionDefinition StoreProductDeactivated = new("StoreProductDeactivated", AuditKept.Forever);

    public static readonly AuditActionDefinition StoreInvoiceIssued = new("StoreInvoiceIssued", AuditKept.Forever);

    public static readonly AuditActionDefinition StorePaymentRecorded = new("StorePaymentRecorded", AuditKept.Forever);

    public static readonly AuditActionDefinition StorePaymentSettled = new("StorePaymentSettled", AuditKept.Forever);

    public static readonly AuditActionDefinition StorePaymentFailed = new("StorePaymentFailed", AuditKept.Forever);

    public static readonly AuditActionDefinition StorePaymentExpired = new("StorePaymentExpired", AuditKept.Forever);

    public static readonly AuditActionDefinition StorePaymentDeleted = new("StorePaymentDeleted", AuditKept.Forever);

    public static readonly AuditActionDefinition StorePaymentsReconciled = new("StorePaymentsReconciled", AuditKept.Forever);

    public static readonly AuditActionDefinition StoreTransferBooked = new("StoreTransferBooked", AuditKept.Forever);

    public static readonly AuditActionDefinition StoreSettingsUpdated = new("StoreSettingsUpdated", AuditKept.Forever);

    // A booking run put an invoiced order's money onto its invoice in Holded, or stopped and said why.
    public static readonly AuditActionDefinition StoreInvoiceBooked = new("StoreInvoiceBooked", AuditKept.Forever);

    // Holded was asked to email an invoice to the camp.
    public static readonly AuditActionDefinition StoreInvoiceSent = new("StoreInvoiceSent", AuditKept.Forever);

    // A Backdoor key pulled a year's order history, which names members.
    public static readonly AuditActionDefinition StoreOrderHistoryExported = new("StoreOrderHistoryExported", AuditKept.Forever);

    // A Store correction path resolved a legacy order's persisted year from its camp season.
    public static readonly AuditActionDefinition StoreOrderYearBackfilled = new("StoreOrderYearBackfilled", AuditKept.Forever);

    // A Store admin's repair run copied a legacy payment's int Method into its string MethodName column.
    public static readonly AuditActionDefinition StorePaymentMethodBackfilled = new("StorePaymentMethodBackfilled", AuditKept.Forever);

    /// <summary>
    /// An order's history as anyone who can view the order sees it: what is logged about the order, its lines, payments
    /// and invoice, plus catalog price changes to its products. Excludes product create/update noise.
    /// </summary>
    public static IReadOnlyList<AuditActionDefinition> OrderHistory { get; } =
    [
        StoreOrderCreated, StoreLineAdded, StoreLineRemoved, StoreCounterpartyEdited, StoreProductPriceChanged,
        StoreInvoiceIssued, StoreInvoiceSent, StorePaymentRecorded, StorePaymentSettled, StorePaymentFailed,
        StorePaymentExpired
    ];

    /// <summary>
    /// Adds the admin-only entries: a deleted payment's entry carries its admin notes and references, and the repair
    /// runs are internal bookkeeping.
    /// </summary>
    public static IReadOnlyList<AuditActionDefinition> OrderHistoryForAdmins { get; } =
        [.. OrderHistory, StoreInvoiceBooked, StorePaymentDeleted, StoreOrderYearBackfilled, StorePaymentMethodBackfilled];
}
