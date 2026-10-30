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

    // A Store correction path resolved a legacy order's persisted year from its camp season.
    public static readonly AuditActionDefinition StoreOrderYearBackfilled = new("StoreOrderYearBackfilled");

    // A Store admin's repair run copied a legacy payment's int Method into its string MethodName column.
    public static readonly AuditActionDefinition StorePaymentMethodBackfilled = new("StorePaymentMethodBackfilled");
}
