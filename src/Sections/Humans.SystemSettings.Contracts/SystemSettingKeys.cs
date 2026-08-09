namespace Humans.SystemSettings.Contracts;

/// <summary>
/// Well-known keys for the SystemSettings table. Part of the section's cross-section
/// surface — the callers that name a key are outside the section.
/// </summary>
public static class SystemSettingKeys
{
    public const string IsEmailSendingPaused = "IsEmailSendingPaused";
    public const string DriveActivityMonitorLastRunAt = "DriveActivityMonitor:LastRunAt";
}
