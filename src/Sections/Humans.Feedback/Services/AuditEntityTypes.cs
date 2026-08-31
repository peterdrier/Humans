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
/// Never regenerate these from <c>nameof</c> — the literal keeps a CLR rename
/// schema-inert (memory/code/type-name-as-persisted-string.md).
/// </para>
/// </remarks>
internal static class AuditEntityTypes
{
    public const string FeedbackReport = "FeedbackReport";
}
