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
/// They were written as <c>nameof(StoreProduct)</c> and friends until the G5 prefix drop
/// (nobodies-collective/Humans#866) renamed those types, which silently changed what the
/// code wrote and queried and orphaned every pre-deploy row. Declaring them as literals
/// is what makes a future rename schema-inert the way the EF <c>ToTable</c> calls already
/// are. Never regenerate these from <c>nameof</c>.
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
