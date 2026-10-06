using Humans.GoogleIntegration.Services;

namespace Humans.GoogleIntegration.Models;

/// <summary>
/// View model for the @nobodies.team email accounts admin page.
/// </summary>
internal sealed class WorkspaceEmailListViewModel
{
    public IReadOnlyList<WorkspaceAccountInfo> Accounts { get; set; } = [];
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
/// One-shot recovery credentials shown to the admin in a modal after a
/// password reset (and optionally a 2FA backup-code grab). Carried in
/// TempData across the PRG redirect so a refresh after dismissal cannot
/// re-expose the secret material.
/// </summary>
internal sealed class WorkspaceRecoveryCredentialsViewModel
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
internal sealed class ProvisionWorkspaceAccountModel
{
    public string EmailPrefix { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
}
