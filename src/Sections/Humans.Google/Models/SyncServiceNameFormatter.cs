using Humans.Google.Contracts;

namespace Humans.Google.Models;

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
