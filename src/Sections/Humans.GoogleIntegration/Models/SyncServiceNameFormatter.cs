using Humans.GoogleIntegration.Contracts;

namespace Humans.GoogleIntegration.Models;

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
