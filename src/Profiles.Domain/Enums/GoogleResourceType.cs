namespace Profiles.Domain.Enums;

/// <summary>
/// Types of Google resources that can be provisioned for members.
/// </summary>
public enum GoogleResourceType
{
    /// <summary>
    /// Folder within a Shared Drive.
    /// </summary>
    DriveFolder = 0,

    /// <summary>
    /// Shared Drive (reserved for future use).
    /// </summary>
    SharedDrive = 1,

    /// <summary>
    /// Google Group for email distribution.
    /// </summary>
    Group = 2
}
