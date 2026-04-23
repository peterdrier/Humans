using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces.GoogleIntegration;

namespace Humans.Application.Services.GoogleIntegration;

/// <summary>
/// Manages @nobodies.team user accounts via the Google Workspace Admin SDK
/// (Directory API), going through <see cref="IWorkspaceUserDirectoryClient"/>
/// so the Application project stays free of <c>Google.Apis.*</c> imports.
/// Migrated to the §15 pattern as part of issue #554 (Google Integration).
/// </summary>
public sealed class GoogleWorkspaceUserService : IGoogleWorkspaceUserService
{
    private readonly IWorkspaceUserDirectoryClient _client;
    private readonly ILogger<GoogleWorkspaceUserService> _logger;

    public GoogleWorkspaceUserService(
        IWorkspaceUserDirectoryClient client,
        ILogger<GoogleWorkspaceUserService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WorkspaceUserAccount>> ListAccountsAsync(
        CancellationToken ct = default)
    {
        var accounts = await _client.ListAccountsAsync(ct);
        _logger.LogInformation("Listed {Count} workspace accounts", accounts.Count);
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

        var created = await _client.ProvisionAccountAsync(
            primaryEmail, firstName, lastName, temporaryPassword, recoveryEmail, ct);

        _logger.LogInformation(
            "Provisioned workspace account: {Email} (recovery: {Recovery})",
            primaryEmail, recoveryEmail ?? "none");

        return created;
    }

    public async Task SuspendAccountAsync(string primaryEmail, CancellationToken ct = default)
    {
        await _client.SuspendAccountAsync(primaryEmail, ct);
        _logger.LogInformation("Suspended workspace account: {Email}", primaryEmail);
    }

    public async Task ReactivateAccountAsync(string primaryEmail, CancellationToken ct = default)
    {
        await _client.ReactivateAccountAsync(primaryEmail, ct);
        _logger.LogInformation("Reactivated workspace account: {Email}", primaryEmail);
    }

    public async Task ResetPasswordAsync(
        string primaryEmail, string newPassword, CancellationToken ct = default)
    {
        await _client.ResetPasswordAsync(primaryEmail, newPassword, ct);
        _logger.LogInformation("Reset password for workspace account: {Email}", primaryEmail);
    }

    public async Task<WorkspaceUserAccount?> GetAccountAsync(
        string primaryEmail, CancellationToken ct = default)
    {
        var account = await _client.GetAccountAsync(primaryEmail, ct);
        if (account is null)
        {
            _logger.LogDebug("Workspace account not found for email {Email}", primaryEmail);
        }
        return account;
    }
}
