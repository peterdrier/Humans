namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Non-sensitive Google Workspace configuration: domain, customer id, default group
/// settings. Credential-sensitive values (service-account key path / inline JSON) stay on
/// <c>Humans.Base.Configuration.GoogleWorkspaceSettings</c>; both bind to the same
/// <c>GoogleWorkspace</c> appsettings section, and the shared property names keep the two
/// bindings identical.
/// </summary>
public sealed class GoogleWorkspaceOptions
{
    /// <summary>
    /// Configuration section name. Matches
    /// <c>Humans.Base.Configuration.GoogleWorkspaceSettings.SectionName</c>.
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
    /// Default group settings applied to every Google Group the system creates
    /// and used as the source of truth for drift detection.
    /// </summary>
    public GoogleWorkspaceGroupOptions Groups { get; set; } = new();
}

/// <summary>
/// Default settings for Google Groups the system provisions. Mirrors
/// <c>Humans.Base.Configuration.GroupSettings</c>.
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
