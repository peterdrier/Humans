namespace Humans.Application.Configuration;

/// <summary>
/// Non-sensitive Google Workspace configuration consumed by the
/// Application-layer <c>GoogleWorkspaceSyncService</c> (§15 Part 2b, issue #575).
/// Credential-sensitive values (service-account key path / inline JSON) stay on
/// <c>Humans.Infrastructure.Configuration.GoogleWorkspaceSettings</c>; both
/// bind to the same <c>GoogleWorkspace</c> appsettings section at DI
/// registration time.
/// </summary>
/// <remarks>
/// Lives in <c>Humans.Application.Configuration</c> so the Application-layer
/// sync service can read domain / customer id / default group settings without
/// reaching into Infrastructure. Mirrors the same field names as the
/// corresponding properties on <c>GoogleWorkspaceSettings</c> so the shared
/// binding path produces identical values.
/// </remarks>
public sealed class GoogleWorkspaceOptions
{
    /// <summary>
    /// Configuration section name. Matches
    /// <c>Humans.Infrastructure.Configuration.GoogleWorkspaceSettings.SectionName</c>.
    /// </summary>
    public const string SectionName = "GoogleWorkspace";

    /// <summary>
    /// The Google Workspace domain (e.g., <c>nobodies.team</c>).
    /// </summary>
    public string Domain { get; set; } = "nobodies.team";

    /// <summary>
    /// Google Workspace customer id (e.g., <c>C024frgt7</c>). Required by the
    /// Cloud Identity Groups API as the parent for group creation.
    /// </summary>
    public string CustomerId { get; set; } = string.Empty;

    /// <summary>
    /// Parent folder id for team folders in Drive. Empty means the Shared
    /// Drive root is used.
    /// </summary>
    public string? TeamFoldersParentId { get; set; }

    /// <summary>
    /// Default group settings applied to every Google Group the system creates
    /// and used as the source of truth for drift detection.
    /// </summary>
    public GoogleWorkspaceGroupOptions Groups { get; set; } = new();
}

/// <summary>
/// Default settings for Google Groups the system provisions. Mirrors
/// <c>Humans.Infrastructure.Configuration.GroupSettings</c>.
/// </summary>
public sealed class GoogleWorkspaceGroupOptions
{
    public string WhoCanJoin { get; set; } = "INVITED_CAN_JOIN";
    public string WhoCanViewMembership { get; set; } = "ALL_MANAGERS_CAN_VIEW";
    public string WhoCanContactOwner { get; set; } = "ALL_MANAGERS_CAN_CONTACT";
    public string WhoCanPostMessage { get; set; } = "ANYONE_CAN_POST";
    public string WhoCanViewGroup { get; set; } = "ALL_MEMBERS_CAN_VIEW";
    public string WhoCanModerateMembers { get; set; } = "OWNERS_AND_MANAGERS";
    public bool AllowExternalMembers { get; set; } = true;
}
