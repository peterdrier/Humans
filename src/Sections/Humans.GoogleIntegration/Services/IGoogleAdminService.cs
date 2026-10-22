using Humans.Base.Attributes;
using Humans.Base.Interfaces;

namespace Humans.GoogleIntegration.Services;

internal interface IGoogleAdminService : IApplicationService
{
    Task<WorkspaceAccountListResult> GetWorkspaceAccountListAsync(
        CancellationToken ct = default);

    /// <summary>Not linked to a user.</summary>
    [ExternalWrite]
    Task<WorkspaceAccountActionResult> ProvisionStandaloneAccountAsync(
        string emailPrefix, string firstName, string lastName,
        Guid actorUserId,
        CancellationToken ct = default);

    /// <summary>
    /// <paramref name="omitEmailFromAudit"/> is for GDPR erasure: the audit log survives it, so that path audits by
    /// actor id alone.
    /// </summary>
    [ExternalWrite]
    Task<WorkspaceAccountActionResult> SuspendAccountAsync(
        string email, Guid actorUserId,
        bool omitEmailFromAudit = false,
        CancellationToken ct = default);

    [ExternalWrite]
    Task<WorkspaceAccountActionResult> ReactivateAccountAsync(
        string email, Guid actorUserId,
        CancellationToken ct = default);

    [ExternalWrite]
    Task<WorkspaceAccountActionResult> ResetPasswordAsync(
        string email, Guid actorUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Also grabs one fresh backup code, so a locked-out member can sign in whatever their 2FA state. Writes two
    /// audit entries.
    /// </summary>
    [ExternalWrite]
    Task<WorkspaceRecoveryCredentialsResult> ResetPasswordAndGenerate2FaAsync(
        string email, Guid actorUserId,
        CancellationToken ct = default);

    Task<WorkspaceAccountActionResult> LinkAccountAsync(
        string email, Guid userId,
        Guid actorUserId,
        CancellationToken ct = default);


    [ExternalWrite]
    Task<GroupLinkActionResult> LinkGroupToTeamAsync(
        Guid teamId, string groupPrefix,
        CancellationToken ct = default);

    Task<IReadOnlyList<TeamSummary>> GetActiveTeamsAsync(
        CancellationToken ct = default);

    /// <summary>Read-only diagnostic with no admin fix: a rename self-heals on the member's next Google sign-in.</summary>
    Task<EmailRenameDetectionResult> DetectEmailRenamesAsync(
        CancellationToken ct = default);
}

internal sealed record WorkspaceAccountListResult(
    IReadOnlyList<WorkspaceAccountInfo> Accounts,
    int TotalAccounts,
    int ActiveAccounts,
    int SuspendedAccounts,
    int LinkedAccounts,
    int UnlinkedAccounts,
    int NotPrimaryCount,
    int MissingTwoFactorCount,
    string? ErrorMessage = null);

internal sealed record WorkspaceAccountInfo(
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

internal sealed record WorkspaceAccountActionResult(
    bool Success,
    string? Message = null,
    string? ErrorMessage = null,
    string? TemporaryPassword = null);

/// <summary>Codes are returned once and cannot be retrieved again.</summary>
internal sealed record WorkspaceBackupCodesResult(
    bool Success,
    string? Email = null,
    IReadOnlyList<string>? Codes = null,
    string? Message = null,
    string? ErrorMessage = null);

/// <summary>One-shot: neither field can be retrieved again once the modal closes.</summary>
internal sealed record WorkspaceRecoveryCredentialsResult(
    bool Success,
    string? Email = null,
    string? TempPassword = null,
    string? BackupCode = null,
    string? Message = null,
    string? ErrorMessage = null);

internal sealed record GroupLinkActionResult(
    bool Success,
    string? Message = null,
    string? InfoMessage = null,
    string? ErrorMessage = null);

internal sealed record TeamSummary(Guid Id, string Name);
