using Humans.Audit.Contracts;

namespace Humans.Settings;

/// <summary>The audit actions Settings writes. Names are stored on audit rows: never rename one.</summary>
internal sealed class SettingsAuditActions : AuditActionContributor
{
    // Every admin event save, make-active and timezone change.
    public static readonly AuditActionDefinition EventSettingsUpdated = new("EventSettingsUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition RetentionPeriodChanged = new("RetentionPeriodChanged", AuditKept.Forever);

    /// <summary>One entry per contributor per daily run that deleted anything, with the count.</summary>
    public static readonly AuditActionDefinition RetentionPeriodDeleted = new("RetentionPeriodDeleted", AuditKept.Forever);
}
