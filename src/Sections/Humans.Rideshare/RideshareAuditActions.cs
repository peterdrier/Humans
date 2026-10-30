using Humans.AuditLog.Contracts;

namespace Humans.Rideshare;

/// <summary>The audit actions Rideshare writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class RideshareAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition RideshareSettingsUpdated = new("RideshareSettingsUpdated");
}
