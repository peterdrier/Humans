namespace Humans.AuditLog.Contracts;

/// <summary>
/// Entity-type strings for audit rows about a person. Persisted and matched exactly when the log is read back (the
/// person's audit panel, GDPR export), so never rename them.
/// </summary>
public static class AuditSubjectTypes
{
    public const string User = "User";

    public const string UserEmail = "UserEmail";
}
