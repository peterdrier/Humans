using Humans.Email.Contracts;
using Humans.Notifications.Contracts;

namespace Humans.GoogleIntegration;

/// <summary>The notification sources this section emits. Names are stored on rows: never change one.</summary>
internal sealed class GoogleIntegrationNotificationSources : NotificationSourceContributor
{
    /// <summary>Google sync or resource provisioning error.</summary>
    public static readonly NotificationSourceDefinition SyncError = new("SyncError", MessageCategory.System);

    public static readonly NotificationSourceDefinition WorkspaceCredentialsReady = new("WorkspaceCredentialsReady", MessageCategory.System);

    /// <summary>Google reconciliation detected and fixed drift.</summary>
    public static readonly NotificationSourceDefinition GoogleDriftDetected = new("GoogleDriftDetected", MessageCategory.System);
}
