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
/// They were written as <c>nameof(CalendarEvent)</c> and <c>nameof(Team)</c>. The first
/// would silently change what the code writes and queries if the entity were ever renamed;
/// the second names an entity in a section that has not moved yet, and stops compiling the
/// day Teams does. Declaring both as literals is what makes those changes schema-inert the
/// way the EF <c>ToTable</c> calls already are (memory/code/type-name-as-persisted-string.md).
/// Never regenerate these from <c>nameof</c>.
/// </para>
/// </remarks>
internal static class AuditEntityTypes
{
    public const string CalendarEvent = "CalendarEvent";
    public const string Team = "Team";
}
