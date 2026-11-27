using Humans.AuditLog.Contracts;

namespace Humans.Google.Contracts;

/// <summary>The audit actions Google writes. Names are stored on audit rows: never rename one.</summary>
public sealed class GoogleAuditActions : AuditActionContributor
{
    public static readonly AuditActionDefinition GoogleResourceAccessGranted = new("GoogleResourceAccessGranted", AuditKept.Forever, QuickFilter: "Access Granted");

    public static readonly AuditActionDefinition GoogleResourceAccessRevoked = new("GoogleResourceAccessRevoked", AuditKept.Forever, QuickFilter: "Access Revoked");

    public static readonly AuditActionDefinition GoogleResourceProvisioned = new("GoogleResourceProvisioned", AuditKept.Forever);

    public static readonly AuditActionDefinition AnomalousPermissionDetected = new("AnomalousPermissionDetected", AuditKept.Forever, Alert: true, QuickFilter: "Anomalous Permissions");

    public static readonly AuditActionDefinition GoogleResourceDeactivated = new("GoogleResourceDeactivated", AuditKept.Forever);

    public static readonly AuditActionDefinition WorkspaceAccountProvisioned = new("WorkspaceAccountProvisioned", AuditKept.Forever);

    public static readonly AuditActionDefinition WorkspaceAccountSuspended = new("WorkspaceAccountSuspended", AuditKept.Forever);

    public static readonly AuditActionDefinition WorkspaceAccountReactivated = new("WorkspaceAccountReactivated", AuditKept.Forever);

    public static readonly AuditActionDefinition WorkspaceAccountPasswordReset = new("WorkspaceAccountPasswordReset", AuditKept.Forever, Verb: "reset Workspace password for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition WorkspaceAccountLinked = new("WorkspaceAccountLinked", AuditKept.Forever);

    public static readonly AuditActionDefinition WorkspaceAccountBackupCodesGenerated = new("WorkspaceAccountBackupCodesGenerated", AuditKept.Forever, Verb: "generated Workspace backup codes for", DescriptionIsTail: true);

    public static readonly AuditActionDefinition WorkspaceAccountResetBlockedFor2Sv = new("WorkspaceAccountResetBlockedFor2Sv", AuditKept.Forever);

    public static readonly AuditActionDefinition GoogleResourceSettingsRemediated = new("GoogleResourceSettingsRemediated", AuditKept.Forever);

    public static readonly AuditActionDefinition GoogleResourceInheritanceDriftCorrected = new("GoogleResourceInheritanceDriftCorrected", AuditKept.Forever);

    public static readonly AuditActionDefinition UserEmailGoogleSet = new("UserEmailGoogleSet", AuditKept.Forever);

    public static readonly AuditActionDefinition GoogleSyncRetryScheduled = new("GoogleSyncRetryScheduled", AuditKept.Forever);

    public static readonly AuditActionDefinition GoogleResourceLinked = new("GoogleResourceLinked", AuditKept.Forever);

    public static readonly AuditActionDefinition GoogleResourceUnlinked = new("GoogleResourceUnlinked", AuditKept.Forever);

    public static readonly AuditActionDefinition GoogleResourceUpdated = new("GoogleResourceUpdated", AuditKept.Forever);

    public static readonly AuditActionDefinition SyncModeChanged = new("SyncModeChanged", AuditKept.Forever);
}
