using Humans.AuditLog.Contracts;

namespace Humans.Cantina;

/// <summary>The audit actions Cantina writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class CantinaAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition CantinaRosterExported = new("CantinaRosterExported");
}
