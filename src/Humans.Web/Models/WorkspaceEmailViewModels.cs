namespace Humans.Web.Models;

/// <summary>
/// View model for the @nobodies.team email accounts admin page.
/// </summary>
public class WorkspaceEmailListViewModel
{
    public List<WorkspaceEmailAccountViewModel> Accounts { get; set; } = [];
    public int TotalAccounts { get; set; }
    public int ActiveAccounts { get; set; }
    public int SuspendedAccounts { get; set; }
    public int NotPrimaryCount { get; set; }
}

/// <summary>
/// Individual @nobodies.team account with matched human info.
/// </summary>
public class WorkspaceEmailAccountViewModel
{
    public string PrimaryEmail { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public bool IsSuspended { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime? LastLoginTime { get; set; }

    /// <summary>
    /// The matched human in the system (if any).
    /// </summary>
    public Guid? MatchedUserId { get; set; }
    public string? MatchedDisplayName { get; set; }

    /// <summary>
    /// Whether the @nobodies.team email is being used as the notification target.
    /// </summary>
    public bool IsUsedAsPrimary { get; set; }
}

/// <summary>
/// Form model for provisioning a new @nobodies.team account.
/// </summary>
public class ProvisionWorkspaceAccountModel
{
    public string EmailPrefix { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
}
