namespace Humans.GoogleIntegration.Services.Workspace;

internal interface IWorkspaceUserDirectoryClient
{
    Task<IReadOnlyList<WorkspaceUserAccount>> ListAccountsAsync(CancellationToken ct = default);

    Task<WorkspaceUserAccount?> GetAccountAsync(string primaryEmail, CancellationToken ct = default);

    /// <summary>Records <paramref name="recoveryEmail"/> only when it is not itself @nobodies.team.</summary>
    Task<WorkspaceUserAccount> ProvisionAccountAsync(
        string primaryEmail,
        string firstName,
        string lastName,
        string temporaryPassword,
        string? recoveryEmail,
        CancellationToken ct = default);

    Task SuspendAccountAsync(string primaryEmail, CancellationToken ct = default);

    Task ReactivateAccountAsync(string primaryEmail, CancellationToken ct = default);

    /// <summary>The account must change its password at next login.</summary>
    Task ResetPasswordAsync(string primaryEmail, string newPassword, CancellationToken ct = default);

    /// <summary>Google reissues the whole set, invalidating earlier codes.</summary>
    Task<IReadOnlyList<string>> GenerateBackupCodesAsync(string primaryEmail, CancellationToken ct = default);
}
