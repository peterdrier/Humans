namespace Humans.Feedback.Services;

/// <summary>
/// The <c>EntityType</c> discriminators Feedback writes to the audit log.
/// </summary>
/// <remarks>
/// <para>
/// These are <b>persisted strings</b>, matched by exact equality when the audit log is
/// read back (<c>IAuditLogService</c> filters on <c>e.EntityType == entityType</c>), so
/// they are a data contract with every row already in the database — not a view of the
/// current CLR type names.
/// </para>
/// <para>
/// They were written as <c>nameof(FeedbackReport)</c>, which would silently change what
/// the code writes and queries if the entity were renamed. Declaring the literal is what
/// makes a rename schema-inert the way the EF <c>ToTable</c> calls already are
/// (memory/code/type-name-as-persisted-string.md). Never regenerate these from
/// <c>nameof</c>.
/// </para>
/// </remarks>
internal static class AuditEntityTypes
{
    public const string FeedbackReport = "FeedbackReport";
}
