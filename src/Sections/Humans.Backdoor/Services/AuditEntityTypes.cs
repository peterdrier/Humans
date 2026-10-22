namespace Humans.Backdoor.Services;

/// <summary>Persisted strings matched exactly when the audit log is read back: never derive them from <c>nameof</c>.</summary>
internal static class AuditEntityTypes
{
    public const string BackdoorApiKey = "BackdoorApiKey";
    public const string User = "User";
}
