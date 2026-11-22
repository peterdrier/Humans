using Humans.AuditLog.Contracts;

namespace Humans.Settings;

/// <summary>The audit actions Settings writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class SettingsAuditActions : AuditActionContributor
{
    // Every admin event save, make-active and timezone change.
    public static readonly AuditActionDefinition EventSettingsUpdated = new("EventSettingsUpdated");
}
