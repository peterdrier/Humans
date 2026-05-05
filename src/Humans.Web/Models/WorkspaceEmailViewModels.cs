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
    public int LinkedAccounts { get; set; }
    public int UnlinkedAccounts { get; set; }
    public int NotPrimaryCount { get; set; }

    /// <summary>
    /// Count of active accounts that have not completed 2-Step Verification enrollment.
    /// These accounts cannot sign in and need attention.
    /// </summary>
    public int MissingTwoFactorCount { get; set; }
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

    /// <summary>
    /// Whether this account has completed 2-Step Verification enrollment.
    /// Unenrolled accounts cannot sign in (2FA is enforced org-wide).
    /// </summary>
    public bool IsEnrolledIn2Sv { get; set; }

    /// <summary>
    /// Personal recovery email Google has on file. Surfaced as a sanity
    /// check so the recovery channel can be validated before lockout.
    /// <c>null</c> when no recovery email is set.
    /// </summary>
    public string? RecoveryEmail { get; set; }
}

/// <summary>
/// One-shot recovery credentials shown to the admin in a modal after a
/// password reset (and optionally a 2FA backup-code grab). Carried in
/// TempData across the PRG redirect so a refresh after dismissal cannot
/// re-expose the secret material.
/// </summary>
public class WorkspaceRecoveryCredentialsViewModel
{
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Freshly generated temporary password for the @nobodies.team account.
    /// Always populated for both flows (reset-only and reset+2FA).
    /// </summary>
    public string TempPassword { get; set; } = string.Empty;

    /// <summary>
    /// Single backup verification code, populated only when the admin
    /// requested the combined "Reset + 2FA" flow. Null for password-only.
    /// </summary>
    public string? BackupCode { get; set; }
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
