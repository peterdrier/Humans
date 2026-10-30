using Humans.AuditLog.Contracts;

namespace Humans.GoogleIntegration.Contracts;

/// <summary>The audit actions GoogleIntegration writes. Names are stored on audit rows: never rename one.</summary>
public sealed class GoogleIntegrationAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition GoogleResourceAccessGranted = new("GoogleResourceAccessGranted", QuickFilter: "Access Granted");

    public static readonly AuditActionDefinition GoogleResourceAccessRevoked = new("GoogleResourceAccessRevoked", QuickFilter: "Access Revoked");

    public static readonly AuditActionDefinition GoogleResourceProvisioned = new("GoogleResourceProvisioned");

    public static readonly AuditActionDefinition AnomalousPermissionDetected = new("AnomalousPermissionDetected", Alert: true, QuickFilter: "Anomalous Permissions");

    public static readonly AuditActionDefinition GoogleResourceDeactivated = new("GoogleResourceDeactivated");

    public static readonly AuditActionDefinition WorkspaceAccountProvisioned = new("WorkspaceAccountProvisioned");

    public static readonly AuditActionDefinition WorkspaceAccountSuspended = new("WorkspaceAccountSuspended");

    public static readonly AuditActionDefinition WorkspaceAccountReactivated = new("WorkspaceAccountReactivated");

    public static readonly AuditActionDefinition WorkspaceAccountPasswordReset = new("WorkspaceAccountPasswordReset", Verb: "reset Workspace password for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition WorkspaceAccountLinked = new("WorkspaceAccountLinked");

    public static readonly AuditActionDefinition WorkspaceAccountBackupCodesGenerated = new("WorkspaceAccountBackupCodesGenerated", Verb: "generated Workspace backup codes for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition WorkspaceAccountResetBlockedFor2Sv = new("WorkspaceAccountResetBlockedFor2Sv");

    public static readonly AuditActionDefinition GoogleResourceSettingsRemediated = new("GoogleResourceSettingsRemediated");

    public static readonly AuditActionDefinition GoogleResourceInheritanceDriftCorrected = new("GoogleResourceInheritanceDriftCorrected");

    public static readonly AuditActionDefinition UserEmailGoogleSet = new("UserEmailGoogleSet");

    public static readonly AuditActionDefinition GoogleSyncRetryScheduled = new("GoogleSyncRetryScheduled");

    public static readonly AuditActionDefinition GoogleResourceLinked = new("GoogleResourceLinked");

    public static readonly AuditActionDefinition GoogleResourceUnlinked = new("GoogleResourceUnlinked");

    public static readonly AuditActionDefinition GoogleResourceUpdated = new("GoogleResourceUpdated");

    public static readonly AuditActionDefinition SyncModeChanged = new("SyncModeChanged");
}
