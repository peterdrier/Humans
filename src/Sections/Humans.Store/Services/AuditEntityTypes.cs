namespace Humans.Store.Services;

/// <summary>
/// Persisted strings, matched by exact equality when the audit log is read back: a contract with existing rows. Never
/// derive them from <c>nameof</c>, or a type rename orphans those rows.
/// </summary>
internal static class AuditEntityTypes
{
    public const string Product = "StoreProduct";
    public const string Order = "StoreOrder";
    public const string OrderLine = "StoreOrderLine";
    public const string Payment = "StorePayment";
    public const string Invoice = "StoreInvoice";
}
