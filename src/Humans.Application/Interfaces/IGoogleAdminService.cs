namespace Humans.Application.Interfaces;

/// <summary>
/// Service for Google Workspace admin operations:
/// workspace account management, group linking, email backfill, account linking.
/// Owns all mutation orchestration and SaveChangesAsync calls for these workflows.
/// </summary>
public interface IGoogleAdminService
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
    /// Links a @nobodies.team account to a user.
    /// </summary>
    Task<WorkspaceAccountActionResult> LinkAccountAsync(
        string email, Guid userId,
        Guid actorUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Applies email backfill corrections for selected users.
    /// </summary>
    Task<EmailBackfillActionResult> ApplyEmailBackfillAsync(
        List<Guid> selectedUserIds, Dictionary<string, string> corrections,
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
    bool IsUsedAsPrimary);

/// <summary>
/// Result of a workspace account action (provision, suspend, reactivate, reset, link).
/// </summary>
public record WorkspaceAccountActionResult(
    bool Success,
    string? Message = null,
    string? ErrorMessage = null,
    string? TemporaryPassword = null);

/// <summary>
/// Result of applying email backfill corrections.
/// </summary>
public record EmailBackfillActionResult(
    int UpdatedCount,
    IReadOnlyList<string> Errors);

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
