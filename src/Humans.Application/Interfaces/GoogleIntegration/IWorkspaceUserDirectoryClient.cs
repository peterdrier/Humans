namespace Humans.Application.Interfaces.GoogleIntegration;

/// <summary>
/// Narrow connector over the Google Workspace Admin SDK (Directory API)
/// scoped to the @nobodies.team user-account operations performed by
/// <see cref="IGoogleWorkspaceUserService"/>. Implementations live in
/// <c>Humans.Infrastructure</c> (real Google-backed implementation for
/// production, stub for dev without service-account credentials). The
/// Application-layer service depends only on this interface so the
/// <c>Humans.Application</c> project stays free of <c>Google.Apis.*</c>
/// imports inside business logic.
/// </summary>
public interface IWorkspaceUserDirectoryClient
{
    /// <summary>
    /// Lists every @nobodies.team account in the configured Workspace domain,
    /// pagination handled internally. Returns shape-neutral DTOs so callers
    /// never see Google SDK types.
    /// </summary>
    Task<IReadOnlyList<WorkspaceUserAccount>> ListAccountsAsync(CancellationToken ct = default);

    /// <summary>
    /// Looks up a single account by its primary email address. Returns
    /// <c>null</c> when no account exists.
    /// </summary>
    Task<WorkspaceUserAccount?> GetAccountAsync(string primaryEmail, CancellationToken ct = default);

    /// <summary>
    /// Provisions a new @nobodies.team account with the given names and
    /// temporary password. When <paramref name="recoveryEmail"/> is provided
    /// and is not itself a @nobodies.team address, the connector records it
    /// on the account for password recovery.
    /// </summary>
    Task<WorkspaceUserAccount> ProvisionAccountAsync(
        string primaryEmail,
        string firstName,
        string lastName,
        string temporaryPassword,
        string? recoveryEmail,
        CancellationToken ct = default);

    /// <summary>
    /// Sets <c>Suspended=true</c> on the account identified by
    /// <paramref name="primaryEmail"/>.
    /// </summary>
    Task SuspendAccountAsync(string primaryEmail, CancellationToken ct = default);

    /// <summary>
    /// Sets <c>Suspended=false</c> on the account identified by
    /// <paramref name="primaryEmail"/>.
    /// </summary>
    Task ReactivateAccountAsync(string primaryEmail, CancellationToken ct = default);

    /// <summary>
    /// Resets the password for the account identified by
    /// <paramref name="primaryEmail"/>. The account is flagged to require a
    /// password change at next login.
    /// </summary>
    Task ResetPasswordAsync(string primaryEmail, string newPassword, CancellationToken ct = default);

    /// <summary>
    /// Generates a fresh set of backup verification codes for the account and
    /// returns them. Google always reissues the full set on generate — any
    /// previously issued codes are invalidated.
    /// </summary>
    Task<IReadOnlyList<string>> GenerateBackupCodesAsync(string primaryEmail, CancellationToken ct = default);

    /// <summary>
    /// Invalidates all backup verification codes for the account.
    /// </summary>
    Task InvalidateBackupCodesAsync(string primaryEmail, CancellationToken ct = default);
}
