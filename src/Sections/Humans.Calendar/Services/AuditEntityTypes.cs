namespace Humans.Calendar.Services;

/// <summary>
/// The <c>EntityType</c> discriminators Calendar writes to the audit log.
/// </summary>
/// <remarks>
/// <para>
/// These are <b>persisted strings</b>, matched by exact equality when the audit log is
/// read back (<c>IAuditLogService</c> filters on <c>e.EntityType == entityType</c>), so
/// they are a data contract with every row already in the database — not a view of the
/// current CLR type names.
/// </para>
/// <para>
/// Never regenerate these from <c>nameof</c>: renaming the CLR type would silently change
/// what the code writes and queries, and <c>nameof(Team)</c> would additionally stop
/// compiling the day Teams moves. Literals keep both changes schema-inert, the way the EF
/// <c>ToTable</c> calls already are (memory/code/type-name-as-persisted-string.md).
/// </para>
/// </remarks>
internal static class AuditEntityTypes
{
    public const string CalendarEvent = "CalendarEvent";
    public const string Team = "Team";
}
