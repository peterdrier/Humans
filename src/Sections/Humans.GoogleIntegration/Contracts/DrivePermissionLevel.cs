namespace Humans.GoogleIntegration.Contracts;

/// <summary>Names match the Drive UI; <c>ToApiRole</c> maps to the API string.</summary>
public enum DrivePermissionLevel
{
    /// <summary>Groups, or not yet set. Zero as the CLR default avoids the EF sentinel trap.</summary>
    None = 0,

    Viewer = 1,

    Commenter = 2,

    Contributor = 3,

    ContentManager = 4,

    Manager = 5
}

public static class DrivePermissionLevelExtensions
{
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
