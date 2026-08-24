namespace Humans.Backdoor.Services;

/// <summary>
/// The <c>EntityType</c> discriminators Backdoor writes to the audit log. Persisted
/// strings, matched by exact equality on read-back — a data contract with the rows already
/// in the database, not a view of the current CLR type names.
/// </summary>
internal static class AuditEntityTypes
{
    public const string BackdoorApiKey = "BackdoorApiKey";
    public const string User = "User";
}
