namespace Humans.Containers.Services;

/// <summary>
/// The <c>EntityType</c> discriminators Containers writes to the audit log.
/// </summary>
/// <remarks>
/// <para>
/// These are <b>persisted strings</b>, matched by exact equality when the audit log is
/// read back (<c>IAuditLogService</c> filters on <c>e.EntityType == entityType</c>), so
/// they are a data contract with every row already in the database — not a view of the
/// current CLR type names. Same rule and same reason as Store's
/// <c>Humans.Store.Services.AuditEntityTypes</c>.
/// </para>
/// <para>
/// <c>Camp</c> is the one Containers cannot express as <c>nameof</c> at all after the G5
/// move (nobodies-collective/Humans#866): the entity belongs to Camps and is not nameable
/// from this assembly. It stays a literal here, as a related-entity discriminator, rather
/// than becoming a reason to reach for Camps' type.
/// </para>
/// </remarks>
internal static class AuditEntityTypes
{
    public const string Container = "Container";
    public const string ContainerPlacement = "ContainerPlacement";
    public const string Camp = "Camp";
}
