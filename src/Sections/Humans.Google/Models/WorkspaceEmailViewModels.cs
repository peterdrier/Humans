namespace Humans.Google.Models;

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
