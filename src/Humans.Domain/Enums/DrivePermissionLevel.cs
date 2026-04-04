namespace Humans.Domain.Enums;

/// <summary>
/// Google Drive permission role level for resources.
/// Display names match Google Drive UI; ToApiRole() maps to the API string.
/// </summary>
public enum DrivePermissionLevel
{
    /// <summary>
    /// Not applicable (Groups) or not yet set. CLR default — avoids EF sentinel trap.
    /// </summary>
    None = 0,

    /// <summary>
    /// Read-only access. Google API role: "reader".
    /// </summary>
    Viewer = 1,

    /// <summary>
    /// Can view and add comments. Google API role: "commenter".
    /// </summary>
    Commenter = 2,

    /// <summary>
    /// Full read/write access. Google API role: "writer".
    /// </summary>
    Contributor = 3,

    /// <summary>
    /// Can organize files within folders. Google API role: "fileOrganizer".
    /// </summary>
    ContentManager = 4,

    /// <summary>
    /// Full management including permissions. Google API role: "organizer".
    /// </summary>
    Manager = 5
}

public static class DrivePermissionLevelExtensions
{
    /// <summary>
    /// Maps the enum to the Google Drive API role string.
    /// </summary>
    public static string ToApiRole(this DrivePermissionLevel level) => level switch
    {
        DrivePermissionLevel.Viewer => "reader",
        DrivePermissionLevel.Commenter => "commenter",
        DrivePermissionLevel.Contributor => "writer",
        DrivePermissionLevel.ContentManager => "fileOrganizer",
        DrivePermissionLevel.Manager => "organizer",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Cannot map None or unknown DrivePermissionLevel to an API role")
    };
}
