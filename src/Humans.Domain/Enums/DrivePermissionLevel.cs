namespace Humans.Domain.Enums;

/// <summary>
/// Google Drive permission role level for resources.
/// Display names match Google Drive UI; ToApiRole() maps to the API string.
/// </summary>
public enum DrivePermissionLevel
{
    /// <summary>
    /// Read-only access. Google API role: "reader".
    /// </summary>
    Viewer = 0,

    /// <summary>
    /// Can view and add comments. Google API role: "commenter".
    /// </summary>
    Commenter = 1,

    /// <summary>
    /// Full read/write access. Google API role: "writer".
    /// </summary>
    Contributor = 2,

    /// <summary>
    /// Can organize files within folders. Google API role: "fileOrganizer".
    /// </summary>
    ContentManager = 3,

    /// <summary>
    /// Full management including permissions. Google API role: "organizer".
    /// </summary>
    Manager = 4
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
        _ => "writer"
    };
}
