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
    /// The email address of a Workspace admin to impersonate.
    /// Required for domain-wide delegation.
    /// </summary>
    public string ImpersonateUser { get; set; } = string.Empty;

    /// <summary>
    /// The Google Workspace domain (e.g., nobodies.team).
    /// </summary>
    public string Domain { get; set; } = "nobodies.team";

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
/// Default settings for Google Groups.
/// </summary>
public class GroupSettings
{
    /// <summary>
    /// Who can view the group membership.
    /// Options: ALL_IN_DOMAIN_CAN_VIEW, ALL_MEMBERS_CAN_VIEW, ALL_MANAGERS_CAN_VIEW
    /// </summary>
    public string WhoCanViewMembership { get; set; } = "ALL_MEMBERS_CAN_VIEW";

    /// <summary>
    /// Who can post to the group.
    /// Options: ANYONE_CAN_POST, ALL_IN_DOMAIN_CAN_POST, ALL_MEMBERS_CAN_POST, ALL_MANAGERS_CAN_POST
    /// </summary>
    public string WhoCanPostMessage { get; set; } = "ALL_MEMBERS_CAN_POST";

    /// <summary>
    /// Whether external members can join.
    /// </summary>
    public bool AllowExternalMembers { get; set; } = true;

    /// <summary>
    /// Whether the group is listed in the directory.
    /// </summary>
    public bool IsArchived { get; set; } = false;
}
