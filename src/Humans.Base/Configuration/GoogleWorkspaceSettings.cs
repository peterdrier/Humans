namespace Humans.Base.Configuration;

public class GoogleWorkspaceSettings
{
    public const string SectionName = "GoogleWorkspace";

    public string ServiceAccountKeyPath { get; set; } = string.Empty;

    /// <summary>Credentials JSON content, the alternative to a key file where file access is restricted.</summary>
    public string? ServiceAccountKeyJson { get; set; }

    public string Domain { get; set; } = "nobodies.team";

    /// <summary>Customer ID (e.g. C024frgt7): the parent the Cloud Identity Groups API requires for group operations.</summary>
    public string CustomerId { get; set; } = string.Empty;

    public bool UseSharedDrives { get; set; } = false;

    public GroupSettings Groups { get; set; } = new();
}

/// <summary>Defaults for Google Groups the system creates.</summary>
public class GroupSettings
{
    public string WhoCanJoin { get; set; } = "INVITED_CAN_JOIN";
    public string WhoCanViewMembership { get; set; } = "ALL_MANAGERS_CAN_VIEW";
    public string WhoCanContactOwner { get; set; } = "ALL_MANAGERS_CAN_CONTACT";
    public string WhoCanPostMessage { get; set; } = "ANYONE_CAN_POST";
    public string WhoCanViewGroup { get; set; } = "ALL_MEMBERS_CAN_VIEW";
    public string WhoCanModerateMembers { get; set; } = "OWNERS_AND_MANAGERS";
    public bool AllowExternalMembers { get; set; } = true;
}
