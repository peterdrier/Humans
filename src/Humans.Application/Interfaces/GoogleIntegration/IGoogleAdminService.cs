using Humans.Application.DTOs;

namespace Humans.Application.Interfaces.GoogleIntegration;

/// <summary>
/// Service for Google Workspace admin operations:
/// workspace account management, group linking, email backfill, account linking.
/// Owns all mutation orchestration and SaveChangesAsync calls for these workflows.
/// </summary>
public interface IGoogleAdminService : IApplicationService
{
    /// <summary>
    /// Builds the workspace accounts list view model with matched user data.
    /// </summary>
    Task<WorkspaceAccountListResult> GetWorkspaceAccountListAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Provisions a new standalone @nobodies.team account (not linked to a user).
    /// </summary>
    Task<WorkspaceAccountActionResult> ProvisionStandaloneAccountAsync(
        string emailPrefix, string firstName, string lastName,
        Guid actorUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Suspends a @nobodies.team account.
    /// </summary>
    Task<WorkspaceAccountActionResult> SuspendAccountAsync(
        string email, Guid actorUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Reactivates a suspended @nobodies.team account.
    /// </summary>
    Task<WorkspaceAccountActionResult> ReactivateAccountAsync(
        string email, Guid actorUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Resets the password for a @nobodies.team account.
    /// </summary>
    Task<WorkspaceAccountActionResult> ResetPasswordAsync(
        string email, Guid actorUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Resets the password for a @nobodies.team account AND grabs a single
    /// fresh backup verification code so the admin can hand both to a
    /// locked-out human in one transaction (the user can sign in with the
    /// temp password and the code regardless of 2FA-enrollment state).
    /// Writes two audit entries (password reset + backup codes generated).
    /// </summary>
    Task<WorkspaceRecoveryCredentialsResult> ResetPasswordAndGenerate2FaAsync(
        string email, Guid actorUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Links a @nobodies.team account to a user.
    /// </summary>
    Task<WorkspaceAccountActionResult> LinkAccountAsync(
        string email, Guid userId,
        Guid actorUserId,
        CancellationToken ct = default);


    /// <summary>
    /// Links a Google Group prefix to a team.
    /// </summary>
    Task<GroupLinkActionResult> LinkGroupToTeamAsync(
        Guid teamId, string groupPrefix,
        CancellationToken ct = default);

    /// <summary>
    /// Gets active teams for the group linking UI.
    /// </summary>
    Task<IReadOnlyList<TeamSummary>> GetActiveTeamsAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Detects @nobodies.team email renames by comparing the stored Google
    /// identity (the verified <c>UserEmail</c> row tagged
    /// <see cref="UserEmail.IsGoogle"/>) against the current
    /// <c>primaryEmail</c> from the Google Directory API. Read-only diagnostic
    /// surface only — there is no admin "fix" action. Renames self-heal on
    /// the user's next Google sign-in via the OAuth callback path.
    /// </summary>
    Task<EmailRenameDetectionResult> DetectEmailRenamesAsync(
        CancellationToken ct = default);
}

/// <summary>
/// Result of loading workspace accounts with matched user data.
/// </summary>
public record WorkspaceAccountListResult(
    IReadOnlyList<WorkspaceAccountInfo> Accounts,
    int TotalAccounts,
    int ActiveAccounts,
    int SuspendedAccounts,
    int LinkedAccounts,
    int UnlinkedAccounts,
    int NotPrimaryCount,
    int MissingTwoFactorCount,
    string? ErrorMessage = null);

/// <summary>
/// Individual workspace account with matched user info.
/// </summary>
public record WorkspaceAccountInfo(
    string PrimaryEmail,
    string FirstName,
    string LastName,
    bool IsSuspended,
    DateTime CreationTime,
    DateTime? LastLoginTime,
    Guid? MatchedUserId,
    string? MatchedDisplayName,
    bool IsUsedAsPrimary,
    bool IsEnrolledIn2Sv,
    string? RecoveryEmail = null);

/// <summary>
/// Result of a workspace account action (provision, suspend, reactivate, reset, link).
/// </summary>
public record WorkspaceAccountActionResult(
    bool Success,
    string? Message = null,
    string? ErrorMessage = null,
    string? TemporaryPassword = null);

/// <summary>
/// Result of generating backup verification codes. Codes are returned once and
/// must be delivered to the human immediately — they cannot be retrieved again.
/// </summary>
public record WorkspaceBackupCodesResult(
    bool Success,
    string? Email = null,
    IReadOnlyList<string>? Codes = null,
    string? Message = null,
    string? ErrorMessage = null);

/// <summary>
/// Result of the combined password-reset + single-backup-code recovery flow.
/// Both fields are one-shot — they can never be retrieved again after
/// the admin closes the modal.
/// </summary>
public record WorkspaceRecoveryCredentialsResult(
    bool Success,
    string? Email = null,
    string? TempPassword = null,
    string? BackupCode = null,
    string? Message = null,
    string? ErrorMessage = null);

/// <summary>
/// Result of linking a group to a team.
/// </summary>
public record GroupLinkActionResult(
    bool Success,
    string? Message = null,
    string? InfoMessage = null,
    string? ErrorMessage = null);

/// <summary>
/// Minimal team info for dropdowns/selectors.
/// </summary>
public record TeamSummary(Guid Id, string Name);
