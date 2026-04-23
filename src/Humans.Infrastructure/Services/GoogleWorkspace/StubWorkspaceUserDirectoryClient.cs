using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces.GoogleIntegration;

namespace Humans.Infrastructure.Services.GoogleWorkspace;

/// <summary>
/// Stub implementation of <see cref="IWorkspaceUserDirectoryClient"/> for
/// development environments without Google Admin SDK credentials. Returns
/// fake data so the admin UI and higher-level workflows can be developed
/// and tested locally. Per the §15 connector pattern, the real
/// <see cref="Humans.Application.Services.GoogleIntegration.GoogleWorkspaceUserService"/>
/// runs against this stub — there is no "stub service" variant.
/// </summary>
public sealed class StubWorkspaceUserDirectoryClient : IWorkspaceUserDirectoryClient
{
    private readonly ILogger<StubWorkspaceUserDirectoryClient> _logger;
    private readonly List<WorkspaceUserAccount> _accounts;

    public StubWorkspaceUserDirectoryClient(ILogger<StubWorkspaceUserDirectoryClient> logger)
    {
        _logger = logger;
        _accounts =
        [
            new WorkspaceUserAccount("alice@nobodies.team", "Alice", "Example", false,
                new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 3, 18, 10, 0, 0, DateTimeKind.Utc)),
            new WorkspaceUserAccount("bob@nobodies.team", "Bob", "Test", false,
                new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 3, 15, 14, 0, 0, DateTimeKind.Utc)),
            new WorkspaceUserAccount("carol@nobodies.team", "Carol", "Demo", true,
                new DateTime(2025, 6, 10, 0, 0, 0, DateTimeKind.Utc),
                null)
        ];
    }

    public Task<IReadOnlyList<WorkspaceUserAccount>> ListAccountsAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("[Stub] Listing {Count} fake @nobodies.team accounts", _accounts.Count);
        return Task.FromResult<IReadOnlyList<WorkspaceUserAccount>>(_accounts.AsReadOnly());
    }

    public Task<WorkspaceUserAccount?> GetAccountAsync(string primaryEmail, CancellationToken ct = default)
    {
        var account = _accounts.FirstOrDefault(a =>
            string.Equals(a.PrimaryEmail, primaryEmail, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(account);
    }

    public Task<WorkspaceUserAccount> ProvisionAccountAsync(
        string primaryEmail,
        string firstName,
        string lastName,
        string temporaryPassword,
        string? recoveryEmail,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[Stub] Provisioned fake account: {Email}", primaryEmail);
        var account = new WorkspaceUserAccount(
            primaryEmail, firstName, lastName, false, DateTime.UtcNow, null);
        _accounts.Add(account);
        return Task.FromResult(account);
    }

    public Task SuspendAccountAsync(string primaryEmail, CancellationToken ct = default)
    {
        _logger.LogInformation("[Stub] Suspended fake account: {Email}", primaryEmail);
        ReplaceAccount(primaryEmail, a => a with { IsSuspended = true });
        return Task.CompletedTask;
    }

    public Task ReactivateAccountAsync(string primaryEmail, CancellationToken ct = default)
    {
        _logger.LogInformation("[Stub] Reactivated fake account: {Email}", primaryEmail);
        ReplaceAccount(primaryEmail, a => a with { IsSuspended = false });
        return Task.CompletedTask;
    }

    public Task ResetPasswordAsync(string primaryEmail, string newPassword, CancellationToken ct = default)
    {
        _logger.LogInformation("[Stub] Reset password for fake account: {Email}", primaryEmail);
        return Task.CompletedTask;
    }

    private void ReplaceAccount(string email, Func<WorkspaceUserAccount, WorkspaceUserAccount> transform)
    {
        var index = _accounts.FindIndex(a =>
            string.Equals(a.PrimaryEmail, email, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _accounts[index] = transform(_accounts[index]);
        }
    }
}
