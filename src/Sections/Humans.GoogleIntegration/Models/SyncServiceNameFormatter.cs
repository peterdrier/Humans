using Humans.GoogleIntegration.Contracts;

namespace Humans.GoogleIntegration.Models;

/// <summary>
/// Display name for a <see cref="SyncServiceType"/> — shared by <c>GoogleController</c>
/// (success messages) and <c>GoogleSyncSettingsTabViewComponent</c> (the /Settings#google-sync
/// tab, peterdrier/Humans#1634).
/// </summary>
internal static class SyncServiceNameFormatter
{
    public static string Format(SyncServiceType type) => type switch
    {
        SyncServiceType.GoogleDrive => "Google Drive",
        SyncServiceType.GoogleGroups => "Google Groups",
        SyncServiceType.Discord => "Discord",
        _ => type.ToString()
    };
}
