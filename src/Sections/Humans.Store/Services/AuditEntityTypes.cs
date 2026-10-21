namespace Humans.Store.Services;

/// <summary>
/// The <c>EntityType</c> discriminators Store writes to the audit log.
/// </summary>
/// <remarks>
/// <para>
/// These are <b>persisted strings</b>, matched by exact equality when the audit log is
/// read back (<c>IAuditLogService</c> filters on <c>e.EntityType == entityType</c>), so
/// they are a data contract with every row already in the database — not a view of the
/// current CLR type names.
/// </para>
/// <para>
/// Declaring them as literals keeps a type rename from silently changing what the code
/// writes and queries and orphaning existing rows. Never regenerate these from <c>nameof</c>.
/// </para>
/// </remarks>
internal static class AuditEntityTypes
{
    public const string Product = "StoreProduct";
    public const string Order = "StoreOrder";
    public const string OrderLine = "StoreOrderLine";
    public const string Payment = "StorePayment";
    public const string Invoice = "StoreInvoice";
}
