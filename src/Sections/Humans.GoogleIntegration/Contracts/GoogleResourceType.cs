namespace Humans.GoogleIntegration.Contracts;

public enum GoogleResourceType
{
    /// <summary>A folder in a Shared Drive.</summary>
    DriveFolder = 0,

    /// <summary>Reserved for future use.</summary>
    SharedDrive = 1,

    Group = 2,

    /// <summary>A single file in a Shared Drive (Sheets, Docs, ...).</summary>
    DriveFile = 3
}
