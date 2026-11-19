using Humans.AuditLog.Contracts;

namespace Humans.Store;

/// <summary>The audit actions Store writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class StoreAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition StoreOrderCreated = new("StoreOrderCreated");

    public static readonly AuditActionDefinition StoreOrderDeleted = new("StoreOrderDeleted");

    public static readonly AuditActionDefinition StoreLineAdded = new("StoreLineAdded");

    public static readonly AuditActionDefinition StoreLineRemoved = new("StoreLineRemoved");

    public static readonly AuditActionDefinition StoreCounterpartyEdited = new("StoreCounterpartyEdited");

    public static readonly AuditActionDefinition StoreProductCreated = new("StoreProductCreated");

    public static readonly AuditActionDefinition StoreProductUpdated = new("StoreProductUpdated");

    public static readonly AuditActionDefinition StoreProductPriceChanged = new("StoreProductPriceChanged");

    public static readonly AuditActionDefinition StoreProductDeactivated = new("StoreProductDeactivated");

    public static readonly AuditActionDefinition StoreInvoiceIssued = new("StoreInvoiceIssued");

    public static readonly AuditActionDefinition StorePaymentRecorded = new("StorePaymentRecorded");

    public static readonly AuditActionDefinition StorePaymentSettled = new("StorePaymentSettled");

    public static readonly AuditActionDefinition StorePaymentFailed = new("StorePaymentFailed");

    public static readonly AuditActionDefinition StorePaymentExpired = new("StorePaymentExpired");

    public static readonly AuditActionDefinition StorePaymentDeleted = new("StorePaymentDeleted");

    public static readonly AuditActionDefinition StorePaymentsReconciled = new("StorePaymentsReconciled");

    public static readonly AuditActionDefinition StoreTransferBooked = new("StoreTransferBooked");

    // A Backdoor key pulled a year's order history, which names members.
    public static readonly AuditActionDefinition StoreOrderHistoryExported = new("StoreOrderHistoryExported");

    // A Store correction path resolved a legacy order's persisted year from its camp season.
    public static readonly AuditActionDefinition StoreOrderYearBackfilled = new("StoreOrderYearBackfilled");

    // A Store admin's repair run copied a legacy payment's int Method into its string MethodName column.
    public static readonly AuditActionDefinition StorePaymentMethodBackfilled = new("StorePaymentMethodBackfilled");

    /// <summary>
    /// An order's history as anyone who can view the order sees it: what is logged about the order, its lines, payments
    /// and invoice, plus catalog price changes to its products. Excludes product create/update noise.
    /// </summary>
    public static IReadOnlyList<AuditActionDefinition> OrderHistory { get; } =
    [
        StoreOrderCreated, StoreLineAdded, StoreLineRemoved, StoreCounterpartyEdited, StoreProductPriceChanged,
        StoreInvoiceIssued, StorePaymentRecorded, StorePaymentSettled, StorePaymentFailed, StorePaymentExpired
    ];

    /// <summary>
    /// Adds the admin-only entries: a deleted payment's entry carries its admin notes and references, and the repair
    /// runs are internal bookkeeping.
    /// </summary>
    public static IReadOnlyList<AuditActionDefinition> OrderHistoryForAdmins { get; } =
        [.. OrderHistory, StorePaymentDeleted, StoreOrderYearBackfilled, StorePaymentMethodBackfilled];
}
