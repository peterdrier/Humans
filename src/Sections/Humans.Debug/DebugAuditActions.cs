using Humans.AuditLog.Contracts;

namespace Humans.Debug;

/// <summary>The audit actions Debug writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class DebugAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition DataMigrationApplied = new("DataMigrationApplied");
}
