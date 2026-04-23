namespace Humans.Application.Interfaces.GoogleIntegration;

/// <summary>
/// Manages @nobodies.team user accounts via Google Workspace Admin SDK (Directory API).
/// </summary>
public interface IGoogleWorkspaceUserService
{
    /// <summary>
    /// Lists all @nobodies.team user accounts from Google Workspace.
    /// </summary>
    Task<IReadOnlyList<WorkspaceUserAccount>> ListAccountsAsync(CancellationToken ct = default);

    /// <summary>
    /// Provisions a new @nobodies.team email account.
    /// Sets the recovery email on the Google account and sends the initial password notification.
    /// </summary>
    /// <param name="primaryEmail">The full email address (e.g., alice@nobodies.team).</param>
    /// <param name="firstName">Given name for the account.</param>
    /// <param name="lastName">Family name for the account.</param>
    /// <param name="temporaryPassword">Temporary password (user must change on first login).</param>
    /// <param name="recoveryEmail">Personal email for password recovery (should not be @nobodies.team).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The provisioned account details.</returns>
    Task<WorkspaceUserAccount> ProvisionAccountAsync(
        string primaryEmail,
        string firstName,
        string lastName,
        string temporaryPassword,
        string? recoveryEmail = null,
        CancellationToken ct = default);

    /// <summary>
    /// Suspends a @nobodies.team account.
    /// </summary>
    Task SuspendAccountAsync(string primaryEmail, CancellationToken ct = default);

    /// <summary>
    /// Reactivates a suspended @nobodies.team account.
    /// </summary>
    Task ReactivateAccountAsync(string primaryEmail, CancellationToken ct = default);

    /// <summary>
    /// Resets the password for a @nobodies.team account.
    /// </summary>
    Task ResetPasswordAsync(string primaryEmail, string newPassword, CancellationToken ct = default);

    /// <summary>
    /// Gets a single account's details.
    /// </summary>
    Task<WorkspaceUserAccount?> GetAccountAsync(string primaryEmail, CancellationToken ct = default);
}

/// <summary>
/// Represents a Google Workspace user account on @nobodies.team.
/// </summary>
public record WorkspaceUserAccount(
    string PrimaryEmail,
    string FirstName,
    string LastName,
    bool IsSuspended,
    DateTime CreationTime,
    DateTime? LastLoginTime);
