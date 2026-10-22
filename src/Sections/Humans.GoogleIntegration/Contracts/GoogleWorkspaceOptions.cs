namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Credentials stay on Base's <c>GoogleWorkspaceSettings</c>. Both bind the same <c>GoogleWorkspace</c> section,
/// so shared property names must stay identical.
/// </summary>
public sealed class GoogleWorkspaceOptions
{
    /// <summary>Must match <c>GoogleWorkspaceSettings.SectionName</c>.</summary>
    public const string SectionName = "GoogleWorkspace";

    public string Domain { get; set; } = "nobodies.team";

    /// <summary>The parent the Cloud Identity Groups API requires for group creation.</summary>
    public string CustomerId { get; set; } = string.Empty;

    /// <summary>Applied to every group the system creates, and the reference for drift detection.</summary>
    public GoogleWorkspaceGroupOptions Groups { get; set; } = new();
}

/// <summary>Mirrors Base's <c>GroupSettings</c>.</summary>
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
