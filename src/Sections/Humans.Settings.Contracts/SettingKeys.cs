namespace Humans.Settings.Contracts;

/// <summary>
/// Well-known keys for the <c>system_settings</c> key/value table. Part of the section's
/// cross-section surface — the callers that name a key are outside the section.
/// </summary>
public static class SettingKeys
{
    public const string IsEmailSendingPaused = "IsEmailSendingPaused";
    public const string DriveActivityMonitorLastRunAt = "DriveActivityMonitor:LastRunAt";
}
