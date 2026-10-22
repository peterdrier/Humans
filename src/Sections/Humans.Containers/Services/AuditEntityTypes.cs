namespace Humans.Containers.Services;

/// <summary>
/// Persisted strings, matched exactly when the audit log is read back: never rename. <c>Camp</c> stays a literal
/// because Camps' type is not nameable from here.
/// </summary>
internal static class AuditEntityTypes
{
    public const string Container = "Container";
    public const string ContainerPlacement = "ContainerPlacement";
    public const string Camp = "Camp";
}
