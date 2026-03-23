namespace Humans.Infrastructure.Configuration;

/// <summary>
/// Configuration for Google Workspace API integration.
/// </summary>
public class GoogleWorkspaceSettings
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "GoogleWorkspace";

    /// <summary>
    /// Path to the service account credentials JSON file.
    /// </summary>
    public string ServiceAccountKeyPath { get; set; } = string.Empty;

    /// <summary>
    /// Service account credentials JSON content (alternative to file path).
    /// Use this for environments where file access is restricted.
    /// </summary>
    public string? ServiceAccountKeyJson { get; set; }

    /// <summary>
    /// The Google Workspace domain (e.g., nobodies.team).
    /// </summary>
    public string Domain { get; set; } = "nobodies.team";

    /// <summary>
    /// Google Workspace customer ID (e.g., C024frgt7).
    /// Required by Cloud Identity Groups API as the parent for group operations.
    /// </summary>
    public string CustomerId { get; set; } = string.Empty;

    /// <summary>
    /// Parent folder ID for team folders in Drive.
    /// If empty, folders are created in the root.
    /// </summary>
    public string? TeamFoldersParentId { get; set; }

    /// <summary>
    /// Whether to use Shared Drives instead of regular folders.
    /// </summary>
    public bool UseSharedDrives { get; set; } = false;

    /// <summary>
    /// Default group settings.
    /// </summary>
    public GroupSettings Groups { get; set; } = new();
}

/// <summary>
/// Default settings for Google Groups created by the system.
/// </summary>
public class GroupSettings
{
    public string WhoCanJoin { get; set; } = "INVITED_CAN_JOIN";
    public string WhoCanViewMembership { get; set; } = "OWNERS_AND_MANAGERS";
    public string WhoCanContactOwner { get; set; } = "ALL_MANAGERS_CAN_CONTACT";
    public string WhoCanPostMessage { get; set; } = "ANYONE_CAN_POST";
    public string WhoCanViewGroup { get; set; } = "ALL_MEMBERS_CAN_VIEW";
    public string WhoCanModerateMembers { get; set; } = "OWNERS_AND_MANAGERS";
    public bool AllowExternalMembers { get; set; } = true;
}
