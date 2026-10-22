using Humans.GoogleIntegration.Services;

namespace Humans.GoogleIntegration.Models;

internal sealed class WorkspaceEmailListViewModel
{
    public IReadOnlyList<WorkspaceAccountInfo> Accounts { get; set; } = [];
    public int TotalAccounts { get; set; }
    public int ActiveAccounts { get; set; }
    public int SuspendedAccounts { get; set; }
    public int LinkedAccounts { get; set; }
    public int UnlinkedAccounts { get; set; }
    public int NotPrimaryCount { get; set; }

    /// <summary>Active accounts without 2-Step Verification, which cannot sign in.</summary>
    public int MissingTwoFactorCount { get; set; }
}

/// <summary>Carried in TempData across the PRG redirect, so a refresh after dismissal cannot re-expose it.</summary>
internal sealed class WorkspaceRecoveryCredentialsViewModel
{
    public string Email { get; set; } = string.Empty;

    public string TempPassword { get; set; } = string.Empty;

    /// <summary>Only for the combined Reset + 2FA flow.</summary>
    public string? BackupCode { get; set; }
}

internal sealed class ProvisionWorkspaceAccountModel
{
    public string EmailPrefix { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
}
