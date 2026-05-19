using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces.GoogleIntegration;

namespace Humans.Application.Services.GoogleIntegration;

/// <summary>
/// Manages @nobodies.team user accounts via the Google Workspace Admin SDK
/// (Directory API), going through <see cref="IWorkspaceUserDirectoryClient"/>
/// so the Application project stays free of <c>Google.Apis.*</c> imports.
/// </summary>
public sealed class GoogleWorkspaceUserService(
    IWorkspaceUserDirectoryClient client,
    ILogger<GoogleWorkspaceUserService> logger) : IGoogleWorkspaceUserService
{
    public async Task<IReadOnlyList<WorkspaceUserAccount>> ListAccountsAsync(
        CancellationToken ct = default)
    {
        var accounts = await client.ListAccountsAsync(ct);
        logger.LogInformation("Listed {Count} workspace accounts", accounts.Count);
        return accounts;
    }

    public async Task<WorkspaceUserAccount> ProvisionAccountAsync(
        string primaryEmail,
        string firstName,
        string lastName,
        string temporaryPassword,
        string? recoveryEmail = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(lastName))
            throw new InvalidOperationException(
                $"Cannot provision account for {primaryEmail}: FamilyName is required but was empty. The user must have a last name in their profile.");

        var created = await client.ProvisionAccountAsync(
            primaryEmail, firstName, lastName, temporaryPassword, recoveryEmail, ct);

        logger.LogInformation(
            "Provisioned workspace account: {Email} (recovery: {Recovery})",
            primaryEmail, recoveryEmail ?? "none");

        return created;
    }

    public async Task SuspendAccountAsync(string primaryEmail, CancellationToken ct = default)
    {
        await client.SuspendAccountAsync(primaryEmail, ct);
        logger.LogInformation("Suspended workspace account: {Email}", primaryEmail);
    }

    public async Task ReactivateAccountAsync(string primaryEmail, CancellationToken ct = default)
    {
        await client.ReactivateAccountAsync(primaryEmail, ct);
        logger.LogInformation("Reactivated workspace account: {Email}", primaryEmail);
    }

    public async Task ResetPasswordAsync(
        string primaryEmail, string newPassword, CancellationToken ct = default)
    {
        await client.ResetPasswordAsync(primaryEmail, newPassword, ct);
        logger.LogInformation("Reset password for workspace account: {Email}", primaryEmail);
    }

    public async Task<WorkspaceUserAccount?> GetAccountAsync(
        string primaryEmail, CancellationToken ct = default)
    {
        var account = await client.GetAccountAsync(primaryEmail, ct);
        if (account is null)
        {
            logger.LogDebug("Workspace account not found for email {Email}", primaryEmail);
        }
        return account;
    }

    public async Task<IReadOnlyList<string>> GenerateBackupCodesAsync(
        string primaryEmail, CancellationToken ct = default)
    {
        var codes = await client.GenerateBackupCodesAsync(primaryEmail, ct);
        logger.LogInformation(
            "Generated {Count} backup code(s) for workspace account: {Email}",
            codes.Count, primaryEmail);
        return codes;
    }

}
