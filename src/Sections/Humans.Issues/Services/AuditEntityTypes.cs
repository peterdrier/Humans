namespace Humans.Issues.Services;

/// <summary>
/// The <c>EntityType</c> discriminators Issues writes to the audit log.
/// </summary>
/// <remarks>
/// <para>
/// These are <b>persisted strings</b>, matched by exact equality when the audit log is
/// read back (<c>IAuditLogService</c> filters on <c>e.EntityType == entityType</c>), so
/// they are a data contract with every row already in the database — not a view of the
/// current CLR type names.
/// </para>
/// <para>Literal, never <c>nameof</c>: an entity rename must not change what is written or queried (memory/code/type-name-as-persisted-string.md).</para>
/// </remarks>
internal static class AuditEntityTypes
{
    public const string Issue = "Issue";
}
