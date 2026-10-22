namespace Humans.Settings.Contracts;

public static class SettingKeys
{
    public const string IsEmailSendingPaused = "IsEmailSendingPaused";
    public const string DriveActivityMonitorLastRunAt = "DriveActivityMonitor:LastRunAt";

    /// <summary>Google Drive folder id the Workgroups section creates each group's subfolder under.</summary>
    // Legacy read fallback; Workgroups saves new state in its own settings table.
    public const string WorkgroupsRootDriveFolderId = "Workgroups:RootDriveFolderId";
}
