namespace Profiles.Infrastructure.Configuration;

/// <summary>
/// Configuration for team resource management authorization.
/// </summary>
public class TeamResourceManagementSettings
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "TeamResourceManagement";

    /// <summary>
    /// Whether metaleads are allowed to manage Google resources for their teams.
    /// When false (default), only Board members can manage team resources.
    /// </summary>
    public bool AllowMetaleadsToManageResources { get; set; } = false;
}
