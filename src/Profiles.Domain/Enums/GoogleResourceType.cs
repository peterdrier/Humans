namespace Profiles.Domain.Enums;

/// <summary>
/// Types of Google resources that can be provisioned for members.
/// </summary>
public enum GoogleResourceType
{
    /// <summary>
    /// Google Drive folder.
    /// </summary>
    DriveFolder = 0,

    /// <summary>
    /// Google Drive shared drive.
    /// </summary>
    SharedDrive = 1,

    /// <summary>
    /// Google Group for email distribution.
    /// </summary>
    Group = 2
}
